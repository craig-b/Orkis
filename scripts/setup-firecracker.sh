#!/usr/bin/env bash
# Provisions the guest assets FirecrackerSandbox needs:
#   $DEST/vmlinux      - uncompressed guest kernel (from the Firecracker CI bucket)
#   $DEST/rootfs.ext4  - Alpine-based rootfs (python3, curl, CA certs, busybox)
#                        implementing the Orkis /init contract
#
# Requires: curl, mkfs.ext4 (e2fsprogs), and bwrap (bubblewrap) to run Alpine's apk
# into the staged rootfs without root. Needs network access to download the kernel,
# the Alpine minirootfs, and packages. Does not require root.
set -euo pipefail

DEST="${ORKIS_FIRECRACKER_HOME:-$HOME/.local/share/orkis/firecracker}"
ARCH="$(uname -m)"
ALPINE_BRANCH="${ORKIS_ALPINE_BRANCH:-v3.21}"
ALPINE_MIRROR="https://dl-cdn.alpinelinux.org/alpine"
KERNEL_URL="https://s3.amazonaws.com/spec.ccfc.min/firecracker-ci/v1.13/${ARCH}/vmlinux-6.1.141"
PACKAGES="${ORKIS_ALPINE_PACKAGES:-python3 curl ca-certificates-bundle}"

if [ "$ARCH" != "x86_64" ] && [ "$ARCH" != "aarch64" ]; then
  echo "Unsupported architecture: $ARCH" >&2
  exit 1
fi
if ! command -v bwrap > /dev/null; then
  echo "bwrap (bubblewrap) is required to build the Alpine rootfs without root." >&2
  exit 1
fi

mkdir -p "$DEST"

# --- Guest kernel ---
if [ ! -f "$DEST/vmlinux" ]; then
  echo "Downloading guest kernel..."
  curl -fL --progress-bar -o "$DEST/vmlinux" "$KERNEL_URL"
else
  echo "Guest kernel already present."
fi

STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT
ROOT="$STAGING/root"
mkdir -p "$ROOT"

# --- Alpine base ---
echo "Fetching Alpine minirootfs ($ALPINE_BRANCH, $ARCH)..."
RELEASES_URL="$ALPINE_MIRROR/$ALPINE_BRANCH/releases/$ARCH"
MINIROOTFS="$(curl -fsSL "$RELEASES_URL/latest-releases.yaml" | grep -oE 'alpine-minirootfs-[0-9.]+-'"$ARCH"'\.tar\.gz' | head -1)"
if [ -z "$MINIROOTFS" ]; then
  echo "Could not determine the latest Alpine minirootfs filename." >&2
  exit 1
fi
curl -fL --progress-bar -o "$STAGING/minirootfs.tar.gz" "$RELEASES_URL/$MINIROOTFS"
tar -xzf "$STAGING/minirootfs.tar.gz" -C "$ROOT"

# --- Install packages via apk, run inside the rootfs with bwrap (no root needed) ---
echo "Installing packages: $PACKAGES"
printf '%s/%s/main\n%s/%s/community\n' "$ALPINE_MIRROR" "$ALPINE_BRANCH" "$ALPINE_MIRROR" "$ALPINE_BRANCH" \
  > "$ROOT/etc/apk/repositories"
cp /etc/resolv.conf "$ROOT/etc/resolv.conf"

# shellcheck disable=SC2086
bwrap \
  --bind "$ROOT" / \
  --proc /proc --dev /dev --tmpfs /tmp \
  --unshare-user --unshare-pid \
  --uid 0 --gid 0 \
  /sbin/apk add --no-cache $PACKAGES

# --- Orkis /init contract + guest agent (see scripts/guest/) ---
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
install -m 0755 "$SCRIPT_DIR/guest/init.sh" "$ROOT/init"
install -m 0644 "$SCRIPT_DIR/guest/orkis-agent.py" -D "$ROOT/opt/orkis-agent.py"
mkdir -p "$ROOT/work"

# Stamp the host↔guest contract version so the sandbox can warn loudly when a
# deployed image drifts behind the host's guest code.
GUEST_VERSION="$(
  sed -n 's/.*GuestContractVersion = \([0-9][0-9]*\);.*/\1/p' \
    "$SCRIPT_DIR/../src/Orkis.Sandbox.Firecracker/FirecrackerSandbox.cs"
)"
if [ -z "$GUEST_VERSION" ]; then
  echo "Could not extract GuestContractVersion from FirecrackerSandbox.cs." >&2
  exit 1
fi
printf '%s\n' "$GUEST_VERSION" > "$ROOT/opt/orkis-guest.version"
echo "Guest contract version: $GUEST_VERSION"

# The rootfs mounts read-only, so DNS config must live somewhere writable: make
# /etc/resolv.conf a symlink into /tmp, written by /init when networking is on.
ln -sf /tmp/resolv.conf "$ROOT/etc/resolv.conf"

# --- Pack into an ext4 image ---
echo "Building rootfs image..."
rm -f "$DEST/rootfs.ext4"
truncate -s 512M "$DEST/rootfs.ext4"
mkfs.ext4 -q -F -d "$ROOT" "$DEST/rootfs.ext4"
resize2fs -M "$DEST/rootfs.ext4" > /dev/null 2>&1 || true

echo
echo "Done. Assets in $DEST:"
ls -lh "$DEST"
echo
echo "Configure Orkis with:"
echo "  KernelImagePath = \"$DEST/vmlinux\""
echo "  RootfsImagePath = \"$DEST/rootfs.ext4\""
