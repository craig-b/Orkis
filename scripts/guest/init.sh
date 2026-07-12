#!/bin/sh
# Orkis /init contract. Two modes, chosen by the host via kernel boot args:
#
#   orkis.mode=agent  — run the vsock guest agent for a warm, reused VM; commands
#                       arrive over vsock and the VM lives until told to shut down.
#   (default)         — legacy boot-per-command: run /work/.orkis/command.sh, emit
#                       the ORKIS output markers on the console, and halt.
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
mount -t proc proc /proc
mount -t sysfs sys /sys
mount -t tmpfs tmpfs /tmp
mount -t devtmpfs dev /dev 2> /dev/null
mount /dev/vdb /work

# Restricted egress: the host passes orkis.net=<addr>/<prefix>:<gateway> when the
# VM has a network device. /etc/resolv.conf is a symlink to /tmp/resolv.conf in
# the (read-only) rootfs, so DNS config can be written at boot.
net="$(sed -n 's/.*orkis\.net=\([^ ]*\).*/\1/p' /proc/cmdline)"
if [ -n "$net" ]; then
  addr="${net%%:*}"
  gateway="${net##*:}"
  ip link set eth0 up
  ip addr add "$addr" dev eth0
  ip route add default via "$gateway"
  printf 'nameserver 1.1.1.1\nnameserver 8.8.8.8\n' > /tmp/resolv.conf
fi

if grep -q orkis.mode=agent /proc/cmdline && [ -f /opt/orkis-agent.py ] && command -v python3 > /dev/null; then
  echo "===ORKIS:AGENT==="
  exec python3 /opt/orkis-agent.py
fi

cd /work
/bin/sh /work/.orkis/command.sh > /tmp/orkis-out 2> /tmp/orkis-err
code=$?
echo "===ORKIS:STDOUT==="
cat /tmp/orkis-out
echo "===ORKIS:STDERR==="
cat /tmp/orkis-err
echo "===ORKIS:EXIT:$code==="
# Unmount /work cleanly so a persistent workspace image's journal is left clean;
# the host falls back to e2fsck replay if this is ever skipped (crash, timeout).
cd /
sync
umount /work 2> /dev/null
# reboot -f (not poweroff): Firecracker has no ACPI; the reboot syscall with
# reboot=k boot args produces the KVM shutdown exit that terminates the VMM.
reboot -f
