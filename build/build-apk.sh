#!/bin/sh
# Build de l'APK dans l'image localify-android-sdk sans toucher aux bin/obj de l'hôte :
#   docker run --rm -v "$PWD:/host:ro" -v "$PWD/out:/out" -v localify-nuget:/root/.nuget/packages \
#     localify-android-sdk sh /host/build/build-apk.sh [Debug|Release]
set -e
CONFIG=${1:-Release}
rm -rf /work && mkdir -p /work
cp -r /host/src /host/Directory.Build.props /host/Directory.Packages.props /host/global.json /work/
find /work/src -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
cd /work
dotnet publish src/Localify.Mobile/Localify.Mobile.csproj -f net10.0-android -c "$CONFIG" -o /out $EXTRA_ARGS
