#!/usr/bin/env python3
"""Download HTML from Azurite or Azure blob storage."""

import argparse
import os
import subprocess
import sys
from pathlib import Path

# Azurite (local development) connection string
AZURITE_CONN = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;"

# Azure storage account (set SCRAPER_STORAGE_ACCOUNT env var for cloud storage access)
AZURE_ACCOUNT = os.environ.get("SCRAPER_STORAGE_ACCOUNT", "<SCRAPER_STORAGE_ACCOUNT>")

def run_az(args: list) -> tuple[str, str, int]:
    """Run az CLI command and return stdout, stderr, returncode."""
    result = subprocess.run(["az"] + args, capture_output=True, text=True)
    return result.stdout, result.stderr, result.returncode

def search_blobs(listing_id: str, source: str = "azurite") -> list[dict]:
    """Search for blobs containing the listing ID."""
    if source == "azurite":
        container = "html"
        args = ["storage", "blob", "list",
                "--container-name", container,
                "--connection-string", AZURITE_CONN,
                "--query", f"[?contains(name, '{listing_id}')]",
                "-o", "json"]
    else:
        container = "scrape-results"
        args = ["storage", "blob", "list",
                "--account-name", AZURE_ACCOUNT,
                "--container-name", container,
                "--query", f"[?contains(name, '{listing_id}')]",
                "-o", "json"]

    stdout, stderr, code = run_az(args)

    if code != 0:
        print(f"Error searching {source}: {stderr}", file=sys.stderr)
        return []

    import json
    try:
        return json.loads(stdout)
    except json.JSONDecodeError:
        return []

def download_blob(blob_name: str, output_path: str, source: str = "azurite") -> bool:
    """Download a specific blob."""
    if source == "azurite":
        container = "html"
        args = ["storage", "blob", "download",
                "--container-name", container,
                "--name", blob_name,
                "--file", output_path,
                "--connection-string", AZURITE_CONN,
                "--no-progress"]
    else:
        container = "scrape-results"
        args = ["storage", "blob", "download",
                "--account-name", AZURE_ACCOUNT,
                "--container-name", container,
                "--name", blob_name,
                "--file", output_path,
                "--no-progress"]

    stdout, stderr, code = run_az(args)
    return code == 0

def find_and_download(listing_id: str, output_dir: str = None) -> str | None:
    """Find and download the main listing HTML for a given ID."""

    # Default output directory
    if output_dir is None:
        output_dir = "AIOMarketMaker.Tests/Data/Listings/Verification"

    output_path = Path(output_dir) / f"{listing_id}.htm"
    output_path.parent.mkdir(parents=True, exist_ok=True)

    # Try Azurite first, then Azure
    for source in ["azurite", "azure"]:
        print(f"Searching {source} for listing {listing_id}...")
        blobs = search_blobs(listing_id, source)

        if not blobs:
            print(f"  No blobs found in {source}")
            continue

        print(f"  Found {len(blobs)} blob(s):")

        # Find the largest blob (main listing page)
        main_blob = None
        max_size = 0

        for blob in blobs:
            size = blob.get("properties", {}).get("contentLength", 0)
            name = blob.get("name", "")
            size_kb = size / 1024

            # Skip description iframes (small files with 'itmdesc' in name)
            if "itmdesc" in name.lower():
                print(f"    {size_kb:.0f} KB - (description iframe, skipping)")
                continue

            print(f"    {size_kb:.0f} KB - {name[-60:]}")

            if size > max_size:
                max_size = size
                main_blob = blob

        if main_blob:
            print(f"\n  Downloading main listing ({max_size/1024:.0f} KB)...")
            if download_blob(main_blob["name"], str(output_path), source):
                print(f"  Saved to: {output_path}")
                print(f"  File size: {output_path.stat().st_size:,} bytes")
                return str(output_path)
            else:
                print("  Download failed")

    print(f"\nNo HTML found for listing {listing_id}")
    return None

def main():
    parser = argparse.ArgumentParser(description="Download eBay listing HTML from blob storage")
    subparsers = parser.add_subparsers(dest="command", required=True)

    # search command
    search_parser = subparsers.add_parser("search", help="Search for blobs by listing ID")
    search_parser.add_argument("listing_id", help="eBay listing ID")
    search_parser.add_argument("--source", choices=["azurite", "azure", "both"], default="both",
                               help="Storage source to search")

    # download command
    dl_parser = subparsers.add_parser("download", help="Download HTML for a listing")
    dl_parser.add_argument("listing_id", help="eBay listing ID")
    dl_parser.add_argument("--output", "-o", help="Output directory")

    args = parser.parse_args()

    if args.command == "search":
        sources = ["azurite", "azure"] if args.source == "both" else [args.source]
        for source in sources:
            blobs = search_blobs(args.listing_id, source)
            print(f"\n{source.upper()} ({len(blobs)} blobs):")
            for blob in blobs:
                size = blob.get("properties", {}).get("contentLength", 0)
                print(f"  {size/1024:.0f} KB - {blob['name'][-80:]}")

    elif args.command == "download":
        find_and_download(args.listing_id, args.output)

if __name__ == "__main__":
    main()
