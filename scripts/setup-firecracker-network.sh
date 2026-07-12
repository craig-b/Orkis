#!/bin/sh
# setup-firecracker-network.sh — one-time privileged host setup for Firecracker
# restricted egress (NetworkMode.RestrictedEgress).
#
# Creates, idempotently:
#   - a bridge (orkis-br0, 172.30.0.1/24) that micro-VM traffic NATs out of,
#   - a pool of persistent TAP devices (orkis-tap0..N-1) owned by the invoking
#     user, so the sandbox can attach VMs to them WITHOUT any privileges,
#   - an nftables table ("orkis") that blocks guest traffic to the host, all
#     private/link-local/multicast ranges (including the cloud metadata address
#     169.254.169.254), allowing public egress only — with one exception: the
#     gateway's port 53, where a dnsmasq forwarder (when installed) lets guests
#     resolve through the host's own resolution,
#   - IPv4 forwarding (persisted via /etc/sysctl.d/99-orkis.conf).
#
# Guests get static addresses 172.30.0.(tap index + 2)/24, gateway 172.30.0.1,
# passed by the sandbox via kernel boot args. No DHCP, no IPv6.
#
# Run:      sudo scripts/setup-firecracker-network.sh
# Undo:     sudo scripts/setup-firecracker-network.sh --remove
#
# Requires: ip (iproute2), nft (nftables). If iptables exists (e.g. Docker
# hosts, where the legacy FORWARD policy is DROP), matching accept rules are
# inserted there too — nftables accept verdicts do not override an iptables
# drop policy, both hooks must pass.
set -eu

BRIDGE="${ORKIS_NET_BRIDGE:-orkis-br0}"
SUBNET_PREFIX="${ORKIS_NET_PREFIX:-172.30.0}" # /24 assumed throughout
TAP_PREFIX="${ORKIS_NET_TAP_PREFIX:-orkis-tap}"
TAP_COUNT="${ORKIS_NET_TAP_COUNT:-8}"
TAP_OWNER="${SUDO_USER:-$(id -un)}"
GATEWAY="$SUBNET_PREFIX.1"

if [ "$(id -u)" != 0 ]; then
  echo "This script must run as root (sudo)." >&2
  exit 1
fi

remove() {
  echo "Removing Orkis network setup..."
  if [ -f /run/orkis-dns.pid ]; then
    kill "$(cat /run/orkis-dns.pid)" 2> /dev/null || true
    rm -f /run/orkis-dns.pid
  fi
  nft delete table inet orkis 2> /dev/null || true
  if command -v iptables > /dev/null; then
    iptables -D FORWARD -i "$BRIDGE" -j ACCEPT 2> /dev/null || true
    iptables -D FORWARD -o "$BRIDGE" -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT 2> /dev/null || true
  fi
  if command -v firewall-cmd > /dev/null && firewall-cmd --state > /dev/null 2>&1; then
    firewall-cmd --zone=trusted --remove-interface="$BRIDGE" > /dev/null 2>&1 || true
    firewall-cmd --permanent --zone=trusted --remove-interface="$BRIDGE" > /dev/null 2>&1 || true
  fi
  i=0
  while [ "$i" -lt "$TAP_COUNT" ]; do
    ip link delete "$TAP_PREFIX$i" 2> /dev/null || true
    i=$((i + 1))
  done
  ip link delete "$BRIDGE" 2> /dev/null || true
  rm -f /etc/sysctl.d/99-orkis.conf
  echo "Done. (IPv4 forwarding was left as-is for this boot.)"
}

if [ "${1:-}" = "--remove" ]; then
  remove
  exit 0
fi

# Refuse to proceed if the subnet is already in use by another interface.
if ip -4 -o addr show | grep -v " $BRIDGE " | grep -q " $SUBNET_PREFIX\."; then
  echo "The subnet $SUBNET_PREFIX.0/24 is already in use by another interface." >&2
  echo "Set ORKIS_NET_PREFIX to a free /24 prefix and re-run (the sandbox's" >&2
  echo "GuestSubnetPrefix option must then match)." >&2
  exit 1
fi

echo "Bridge $BRIDGE ($GATEWAY/24)..."
ip link add "$BRIDGE" type bridge 2> /dev/null || true
ip addr replace "$GATEWAY/24" dev "$BRIDGE"
ip link set "$BRIDGE" up

echo "TAP devices $TAP_PREFIX{0..$((TAP_COUNT - 1))} owned by $TAP_OWNER..."
i=0
while [ "$i" -lt "$TAP_COUNT" ]; do
  tap="$TAP_PREFIX$i"
  ip tuntap add dev "$tap" mode tap user "$TAP_OWNER" 2> /dev/null || true
  ip link set "$tap" master "$BRIDGE"
  ip link set "$tap" up
  i=$((i + 1))
done

echo "IPv4 forwarding..."
sysctl -q -w net.ipv4.ip_forward=1
printf 'net.ipv4.ip_forward = 1\n' > /etc/sysctl.d/99-orkis.conf

echo "nftables rules..."
nft -f - << EOF
table inet orkis
delete table inet orkis
table inet orkis {
  chain input {
    type filter hook input priority -10; policy accept;
    # Guests resolve through the DNS forwarder on the gateway; everything else
    # on the host stays unreachable.
    iifname "$BRIDGE" ip daddr $GATEWAY udp dport 53 accept
    iifname "$BRIDGE" ip daddr $GATEWAY tcp dport 53 accept
    # Guests may not talk to the host itself, on any other port.
    iifname "$BRIDGE" drop
  }
  chain forward {
    type filter hook forward priority -10; policy accept;
    # No routed guest-to-guest traffic (same-bridge L2 traffic is unaffected).
    iifname "$BRIDGE" oifname "$BRIDGE" drop
    # Public egress only: block private, loopback, link-local (incl. the cloud
    # metadata address 169.254.169.254), CGNAT, multicast, and reserved ranges.
    iifname "$BRIDGE" ip daddr { 0.0.0.0/8, 10.0.0.0/8, 100.64.0.0/10, 127.0.0.0/8, 169.254.0.0/16, 172.16.0.0/12, 192.168.0.0/16, 224.0.0.0/4, 240.0.0.0/4 } drop
    iifname "$BRIDGE" accept
    oifname "$BRIDGE" ct state established,related accept
    oifname "$BRIDGE" drop
  }
  chain postrouting {
    type nat hook postrouting priority 100;
    ip saddr $SUBNET_PREFIX.0/24 oifname != "$BRIDGE" masquerade
  }
}
EOF

if command -v iptables > /dev/null; then
  echo "iptables FORWARD accepts (for hosts where its policy is DROP, e.g. Docker)..."
  iptables -C FORWARD -i "$BRIDGE" -j ACCEPT 2> /dev/null \
    || iptables -I FORWARD -i "$BRIDGE" -j ACCEPT
  iptables -C FORWARD -o "$BRIDGE" -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT 2> /dev/null \
    || iptables -I FORWARD -o "$BRIDGE" -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT
fi

# A DNS forwarder on the gateway lets guests resolve through the host's own
# resolution (which works wherever the host's does) instead of depending on
# public resolvers, which some hosts block. Guests list the gateway first with
# public fallbacks, so this is an upgrade, not a requirement.
DNSMASQ_PID=/run/orkis-dns.pid
if command -v dnsmasq > /dev/null; then
  echo "DNS forwarder (dnsmasq) on $GATEWAY:53..."
  if [ -f "$DNSMASQ_PID" ]; then
    kill "$(cat "$DNSMASQ_PID")" 2> /dev/null || true
    rm -f "$DNSMASQ_PID"
  fi
  dnsmasq --port=53 --listen-address="$GATEWAY" --bind-interfaces --no-hosts --pid-file="$DNSMASQ_PID"
else
  echo "dnsmasq not found: guests will use public DNS resolvers directly, which"
  echo "hosts that restrict outbound DNS may block. Install dnsmasq and re-run."
fi

# firewalld evaluates its own forward chains for every table, and an unzoned
# bridge falls into the default zone, which drops forwarded traffic. Trusting the
# bridge hands filtering back to the orkis table above (host/LAN/metadata blocks
# still apply) instead of double-filtering.
if command -v firewall-cmd > /dev/null && firewall-cmd --state > /dev/null 2>&1; then
  echo "firewalld: placing $BRIDGE in the trusted zone..."
  firewall-cmd --zone=trusted --change-interface="$BRIDGE" > /dev/null
  firewall-cmd --permanent --zone=trusted --change-interface="$BRIDGE" > /dev/null
fi

echo "Done. The Orkis Firecracker sandbox can now use NetworkMode.RestrictedEgress"
echo "without privileges. Note: nftables rules and TAP/bridge devices do not survive"
echo "a reboot; re-run this script after rebooting (it is idempotent)."
