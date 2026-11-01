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
swiftc -O -o build/ClipboardHistory.app/Contents/MacOS/ClipboardHistory main.swift
cp Info.plist build/ClipboardHistory.app/Contents/Info.plist
codesign --force --deep --sign - build/ClipboardHistory.app
build/ClipboardHistory.app/Contents/MacOS/ClipboardHistory --self-test
echo "Built ${PWD}/build/ClipboardHistory.app"
