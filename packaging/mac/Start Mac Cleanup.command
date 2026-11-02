#!/bin/zsh
# Night Tech Mac Cleanup launcher: picks the right program for this Mac (Apple chip or Intel) and runs it as administrator.
cd "$(dirname "$0")"
if [[ "$(uname -m)" == "arm64" ]]; then app="./arm64/NightTechMacCleanup"; else app="./x64/NightTechMacCleanup"; fi
# Files copied from the internet are marked "quarantined"; clear that for our own program only.
xattr -dr com.apple.quarantine "./arm64" "./x64" 2>/dev/null
chmod +x "$app"
echo "Night Tech Mac Cleanup needs your Mac password (the administrator's) to clean every account and run the checks."
sudo "$app"
