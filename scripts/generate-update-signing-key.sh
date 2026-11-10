#!/usr/bin/env bash
# One-time setup for in-app updates: create the ECDSA P-256 key that signs update-manifest.json.
#   - the private key goes to the UPDATE_SIGNING_KEY Actions secret (and a local backup you choose);
#   - the public key is written into the app (src/RailReader2/Services/Updates/update-signing-key.pub.pem).
# Keep the backup somewhere safe (a password manager). If it is lost, a new key can only reach users
# through a release they install by hand: builds trust only the key they were built with.
set -euo pipefail

backup="${1:-$HOME/.config/railreader2-release/update-signing-key.pem}"
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
pub="$repo_root/src/RailReader2/Services/Updates/update-signing-key.pub.pem"

if [[ -e "$backup" ]]; then
  echo "Refusing to overwrite an existing key at $backup" >&2
  exit 1
fi
umask 077
mkdir -p "$(dirname "$backup")"
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out "$backup"
openssl pkey -in "$backup" -pubout -out "$pub"
gh secret set UPDATE_SIGNING_KEY --repo sjvrensburg/railreader2 < "$backup"

echo "Private key backup: $backup"
echo "Public key written: $pub (commit it)"
echo "Actions secret UPDATE_SIGNING_KEY set."
