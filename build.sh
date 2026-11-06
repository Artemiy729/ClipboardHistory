#!/bin/zsh
set -euo pipefail
cd "${0:A:h}"
sdk26=/Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk
export SDKROOT="${SDKROOT:-$(if [[ -d "$sdk26" && "$(swift --version)" == *'Swift version 6.3'* ]]; then print -r -- "$sdk26"; else xcrun --sdk macosx --show-sdk-path; fi)}"
export CLANG_MODULE_CACHE_PATH=/private/tmp/clipboard-history-clang-cache
export SWIFT_MODULE_CACHE_PATH=/private/tmp/clipboard-history-swift-cache
mkdir -p build/ClipboardHistory.app/Contents/MacOS
mkdir -p build/ClipboardHistory.app/Contents/Resources
mkdir -p build/AppIcon.iconset
swift make-icon.swift assets/icon.png build/AppIcon.iconset
python3 make-icns.py build/AppIcon.iconset build/ClipboardHistory.app/Contents/Resources/AppIcon.icns
build_arch="${CLIPBOARD_BUILD_ARCH:-$(uname -m)}"
sources=(main.swift HistoryStorage.swift HistoryPreviews.swift StorageTests.swift)
if [[ "$build_arch" == universal ]]; then
    for architecture in arm64 x86_64; do
        swiftc -O -target "$architecture-apple-macosx14.0" -o "build/ClipboardHistory-$architecture" "${sources[@]}"
    done
    lipo -create build/ClipboardHistory-arm64 build/ClipboardHistory-x86_64 -output build/ClipboardHistory.app/Contents/MacOS/ClipboardHistory
else
    swiftc -O -target "$build_arch-apple-macosx14.0" -o build/ClipboardHistory.app/Contents/MacOS/ClipboardHistory "${sources[@]}"
fi
cp Info.plist build/ClipboardHistory.app/Contents/Info.plist
codesign --force --deep --options runtime --sign - build/ClipboardHistory.app
build/ClipboardHistory.app/Contents/MacOS/ClipboardHistory --self-test
echo "Built ${PWD}/build/ClipboardHistory.app"
