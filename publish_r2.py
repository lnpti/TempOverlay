"""Publica o instalador no espelho Cloudflare R2 (bucket app-releases, prefixo tempoverlay).
Credenciais: variaveis R2_ACCESS_KEY_ID/R2_SECRET_ACCESS_KEY/R2_ENDPOINT ou o arquivo ~/.playnews/r2.env.
Uso: python publish_r2.py   (depois do build.ps1)
"""
import json
import os
import re
import sys

import boto3

BUCKET = "app-releases"
PREFIX = "tempoverlay"
PUBLIC_BASE = f"https://pub-8060abbe70084968817647c74ce4ffbc.r2.dev/{PREFIX}"
HERE = os.path.dirname(os.path.abspath(__file__))


def load_env():
    path = os.path.join(os.path.expanduser("~"), ".playnews", "r2.env")
    if os.path.exists(path):
        for line in open(path, encoding="utf-8"):
            k, _, v = line.strip().partition("=")
            if k and v and k not in os.environ:
                os.environ[k] = v


def main():
    load_env()
    version = re.search(r'Version = "([^"]+)"', open(os.path.join(HERE, "TempOverlay.cs"), encoding="utf-8").read()).group(1)
    name = f"TempOverlay_Setup_v{version}.exe"
    path = os.path.join(HERE, "dist", name)
    if not os.path.exists(path):
        sys.exit(f"Nao encontrado: {path}. Rode build.ps1 antes.")
    s3 = boto3.client("s3", endpoint_url=os.environ["R2_ENDPOINT"], region_name="auto",
                      aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
                      aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"])
    print(f"Enviando {name} ({os.path.getsize(path) / 1e6:.1f} MB)")
    with open(path, "rb") as f:
        s3.put_object(Bucket=BUCKET, Key=f"{PREFIX}/{name}", Body=f, ContentType="application/octet-stream")
    manifest = {"version": version, "installer_url": f"{PUBLIC_BASE}/{name}", "size": os.path.getsize(path)}
    s3.put_object(Bucket=BUCKET, Key=f"{PREFIX}/latest.json", ContentType="application/json",
                  Body=json.dumps(manifest, ensure_ascii=False, indent=2).encode("utf-8"))
    print("Publicado:", manifest["installer_url"])
    print("Manifesto:", f"{PUBLIC_BASE}/latest.json")


if __name__ == "__main__":
    main()
