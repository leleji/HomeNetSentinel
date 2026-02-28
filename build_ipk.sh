#!/usr/bin/env sh
set -eu

PKG_NAME="luci-app-homenet-sentinel"
PKG_VERSION="${PKG_VERSION:-1.0.0}"
PKG_RELEASE="${PKG_RELEASE:-1}"
PKG_ARCH="${PKG_ARCH:-all}"
PKG_FULL_VERSION="${PKG_VERSION}-${PKG_RELEASE}"

ROOT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
PKG_SRC_DIR="$ROOT_DIR/package/luci-app-homenet-sentinel"
FILES_DIR="$PKG_SRC_DIR/files"
PREBUILT_BIN="$PKG_SRC_DIR/prebuilt/HomeNet-Sentinel"
IPKG_BUILD_BIN="ipkg-build"

BUILD_ROOT="$ROOT_DIR/.ipkbuild"
PKG_DIR="$BUILD_ROOT/${PKG_NAME}"
CONTROL_DIR="$PKG_DIR/CONTROL"
OUT_DIR="$ROOT_DIR/bin-ipk"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --ipkg-build)
      [ "$#" -ge 2 ] || {
        echo "ERROR: --ipkg-build requires a path argument" >&2
        exit 1
      }
      IPKG_BUILD_BIN="$2"
      shift 2
      ;;
    --output|-o)
      [ "$#" -ge 2 ] || {
        echo "ERROR: --output requires a directory argument" >&2
        exit 1
      }
      OUT_DIR="$2"
      shift 2
      ;;
    --bin)
      [ "$#" -ge 2 ] || {
        echo "ERROR: --bin requires a binary path argument" >&2
        exit 1
      }
      PREBUILT_BIN="$2"
      shift 2
      ;;
    --arch)
      [ "$#" -ge 2 ] || {
        echo "ERROR: --arch requires an architecture argument" >&2
        exit 1
      }
      PKG_ARCH="$2"
      shift 2
      ;;
    --version)
      [ "$#" -ge 2 ] || {
        echo "ERROR: --version requires a version string" >&2
        exit 1
      }
      PKG_FULL_VERSION="$2"
      shift 2
      ;;
    --help|-h)
      cat <<'EOF'
Usage: ./build_ipk.sh [options]

Options:
  --bin <path>          Use specific HomeNet-Sentinel binary
  --arch <arch>         Package architecture (e.g. x86_64)
  --version <ver-rel>   Package version-release (e.g. 1.0.3-0)
  --ipkg-build <path>   Use specific ipkg-build binary/script
  --output, -o <dir>    Output directory for .ipk
  --help, -h            Show this help

Environment overrides:
  PKG_VERSION, PKG_RELEASE, PKG_ARCH
EOF
      exit 0
      ;;
    *)
      echo "ERROR: Unknown argument: $1" >&2
      echo "Try: ./build_ipk.sh --help" >&2
      exit 1
      ;;
  esac
done

if [ "$IPKG_BUILD_BIN" = "ipkg-build" ]; then
  if ! command -v ipkg-build >/dev/null 2>&1; then
    echo "ERROR: ipkg-build not found in PATH" >&2
    exit 1
  fi
else
  if [ ! -f "$IPKG_BUILD_BIN" ]; then
    echo "ERROR: ipkg-build not found: $IPKG_BUILD_BIN" >&2
    exit 1
  fi
fi

if [ ! -f "$PREBUILT_BIN" ]; then
  echo "ERROR: Missing binary: $PREBUILT_BIN" >&2
  echo "Place your OpenWrt-compatible HomeNet-Sentinel binary there first." >&2
  exit 1
fi

rm -rf "$PKG_DIR"
mkdir -p "$CONTROL_DIR" "$OUT_DIR"

# Copy package files layout
cp -a "$FILES_DIR/." "$PKG_DIR/"

# Install binary
mkdir -p "$PKG_DIR/usr/bin"
cp "$PREBUILT_BIN" "$PKG_DIR/usr/bin/HomeNet-Sentinel"
chmod 0755 "$PKG_DIR/usr/bin/HomeNet-Sentinel"

# Ensure init script executable
if [ -f "$PKG_DIR/etc/init.d/homenet-sentinel" ]; then
  chmod 0755 "$PKG_DIR/etc/init.d/homenet-sentinel"
fi

# CONTROL metadata
cat > "$CONTROL_DIR/control" <<EOF
Package: $PKG_NAME
Version: $PKG_FULL_VERSION
Architecture: $PKG_ARCH
Maintainer: HomeNet Sentinel
Section: net
Priority: optional
Depends: libc, libpthread, luci-base
Description: HomeNet Sentinel service with LuCI UI for MQTT presence and WAN telemetry.
EOF

cat > "$CONTROL_DIR/postinst" <<'EOF'
#!/bin/sh
exit 0
EOF
chmod 0755 "$CONTROL_DIR/postinst"

if [ -f "$PKG_DIR/etc/config/homenet-sentinel" ]; then
  cat > "$CONTROL_DIR/conffiles" <<'EOF'
/etc/config/homenet-sentinel
EOF
fi

echo "Building IPK..."
if [ "$IPKG_BUILD_BIN" = "ipkg-build" ]; then
  ipkg-build "$PKG_DIR" "$OUT_DIR"
elif [ -x "$IPKG_BUILD_BIN" ]; then
  "$IPKG_BUILD_BIN" "$PKG_DIR" "$OUT_DIR"
else
  bash "$IPKG_BUILD_BIN" "$PKG_DIR" "$OUT_DIR"
fi

echo
echo "Done. Output:"
ls -lh "$OUT_DIR"/*.ipk
