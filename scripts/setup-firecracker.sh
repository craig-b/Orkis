#!/usr/bin/env bash
# Provisions the guest assets FirecrackerSandbox needs:
#   $DEST/vmlinux      - uncompressed guest kernel (from the Firecracker CI bucket)
#   $DEST/rootfs.ext4  - minimal busybox rootfs implementing the Orkis /init contract
#
# Requires: curl, mkfs.ext4 (e2fsprogs), and either a static busybox on the host
# or network access to download one. Does not require root.
set -euo pipefail

DEST="${ORKIS_FIRECRACKER_HOME:-$HOME/.local/share/orkis/firecracker}"
ARCH="$(uname -m)"
KERNEL_URL="https://s3.amazonaws.com/spec.ccfc.min/firecracker-ci/v1.13/${ARCH}/vmlinux-6.1.141"
BUSYBOX_URL="https://busybox.net/downloads/binaries/1.35.0-${ARCH}-linux-musl/busybox"

if [ "$ARCH" != "x86_64" ] && [ "$ARCH" != "aarch64" ]; then
  echo "Unsupported architecture: $ARCH" >&2
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

# --- Busybox: prefer a static host binary, download otherwise ---
STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT

BUSYBOX="$STAGING/busybox"
if command -v busybox > /dev/null && file "$(command -v busybox)" | grep -q 'statically linked'; then
  echo "Using host busybox ($(command -v busybox))."
  cp "$(command -v busybox)" "$BUSYBOX"
else
  echo "Downloading static busybox..."
  curl -fL --progress-bar -o "$BUSYBOX" "$BUSYBOX_URL"
fi
chmod +x "$BUSYBOX"

# --- Rootfs implementing the Orkis /init contract ---
echo "Building rootfs..."
ROOT="$STAGING/root"
mkdir -p "$ROOT"/{bin,sbin,dev,proc,sys,tmp,work,etc}
cp "$BUSYBOX" "$ROOT/bin/busybox"
for applet in sh cat mount umount reboot printenv ls; do
  ln -sf busybox "$ROOT/bin/$applet"
done

cat > "$ROOT/init" << 'EOF'
#!/bin/sh
export PATH=/bin:/sbin
/bin/busybox mount -t proc proc /proc
/bin/busybox mount -t sysfs sys /sys
/bin/busybox mount -t tmpfs tmpfs /tmp
/bin/busybox mount -t devtmpfs dev /dev 2> /dev/null
/bin/busybox mount /dev/vdb /work
cd /work
/bin/sh /work/.orkis/command.sh > /tmp/orkis-out 2> /tmp/orkis-err
code=$?
echo "===ORKIS:STDOUT==="
/bin/busybox cat /tmp/orkis-out
echo "===ORKIS:STDERR==="
/bin/busybox cat /tmp/orkis-err
echo "===ORKIS:EXIT:$code==="
# reboot -f (not poweroff): Firecracker has no ACPI; the reboot syscall with
# reboot=k boot args produces the KVM shutdown exit that terminates the VMM.
/bin/busybox reboot -f
EOF
chmod +x "$ROOT/init"

rm -f "$DEST/rootfs.ext4"
truncate -s 16M "$DEST/rootfs.ext4"
mkfs.ext4 -q -F -d "$ROOT" "$DEST/rootfs.ext4"

echo
echo "Done. Assets in $DEST:"
ls -lh "$DEST"
echo
echo "Configure Orkis with:"
echo "  KernelImagePath = \"$DEST/vmlinux\""
echo "  RootfsImagePath = \"$DEST/rootfs.ext4\""
