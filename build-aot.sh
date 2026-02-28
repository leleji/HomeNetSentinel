#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 4 ]; then
  echo "Usage:"
  echo "  $0 <WIN_PROJ_DIR> <PROJ_FILE> <RUNTIME> <LINUX_BUILD_ROOT>"
  exit 1
fi

WIN_PROJ_DIR="$1"
PROJ_FILE="$2"
RUNTIME="$3"
LINUX_BUILD_ROOT="$4"

WIN_PROJ_DIR="$(realpath "$WIN_PROJ_DIR")"
LINUX_BUILD_ROOT="$(realpath -m "$LINUX_BUILD_ROOT")"

IMAGE="dotnet-aot-alpine"
WORK_DIR="$LINUX_BUILD_ROOT/work"
OBJ_DIR="$LINUX_BUILD_ROOT/obj"
BIN_DIR="$LINUX_BUILD_ROOT/bin"
PUB_DIR="$LINUX_BUILD_ROOT/out-musl"
HOME_DIR="$LINUX_BUILD_ROOT/home"
NUGET_DIR="$LINUX_BUILD_ROOT/nuget" 

mkdir -p "$WORK_DIR" "$OBJ_DIR" "$BIN_DIR" "$PUB_DIR" "$HOME_DIR" "$NUGET_DIR"

echo "======================================"
echo " Source (Windows): $WIN_PROJ_DIR"
echo " Work (Linux)    : $WORK_DIR"
echo " Obj (Linux)     : $OBJ_DIR"
echo " Bin (Linux)     : $BIN_DIR"
echo " Publish (Linux) : $PUB_DIR"
echo " Dotnet HOME     : $HOME_DIR"
echo " NuGet cache     : $NUGET_DIR"
echo " Runtime         : $RUNTIME"
echo " Image           : $IMAGE"
echo "======================================"

if [[ ! -f "$WIN_PROJ_DIR/$PROJ_FILE" ]]; then
  echo "ERROR: Cannot find $PROJ_FILE in $WIN_PROJ_DIR" >&2
  exit 1
fi

rm -rf "$WORK_DIR"/*
mkdir -p "$WORK_DIR"

docker run --rm \
  --user "$(id -u):$(id -g)" \
  -e HOME=/build/home \
  -e DOTNET_CLI_HOME=/build/home \
  -e DOTNET_NOLOGO=1 \
  -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  -e NUGET_PACKAGES=/build/nuget \
  -e NUGET_HTTP_TIMEOUT_SECONDS=300 \
  -e NUGET_XMLDOC_MODE=skip \
  -v "$WIN_PROJ_DIR":/src:ro \
  -v "$WORK_DIR":/build/work \
  -v "$OBJ_DIR":/build/obj \
  -v "$BIN_DIR":/build/bin \
  -v "$PUB_DIR":/build/publish \
  -v "$HOME_DIR":/build/home \
  -v "$NUGET_DIR":/build/nuget \
  "$IMAGE" \
  sh -lc '
    set -e
    echo "==> Container user: $(id)"
    echo "==> HOME=$HOME"
    echo "==> DOTNET_CLI_HOME=$DOTNET_CLI_HOME"
    echo "==> NUGET_PACKAGES=$NUGET_PACKAGES"

    echo "==> Copy source to /build/work"
    cp -a /src/. /build/work/
    cd /build/work
    rm -rf obj bin || true

    echo "==> Restore first"
    dotnet restore ./'"$PROJ_FILE"' -r '"$RUNTIME"' -v minimal

    ILC="/build/nuget/runtime.$RUNTIME.microsoft.dotnet.ilcompiler/10.0.2/tools/ilc"
    if [ -f "$ILC" ]; then
      echo "==> Fix ilc permission: $ILC"
      chmod +x "$ILC" || true
      ls -l "$ILC" || true
    fi

    echo "==> Publish AOT"
    dotnet publish ./'"$PROJ_FILE"' \
      -c Release \
      -r '"$RUNTIME"' \
      -p:PublishAot=true \
      -p:SelfContained=true \
      -p:BaseIntermediateOutputPath=/build/obj/ \
      -p:BaseOutputPath=/build/bin/ \
      -p:PublishDir=/build/publish/ \
      -v minimal

    echo "==> Publish result:"
    ls -lah /build/publish
  '

echo
echo "========== DONE =========="
echo "Output files:"
ls -lh "$PUB_DIR"
