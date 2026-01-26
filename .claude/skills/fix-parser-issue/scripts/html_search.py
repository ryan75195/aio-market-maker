#!/usr/bin/env python3
"""Search HTML files for parser-relevant patterns."""

import argparse
import re
import sys
from pathlib import Path

# Known eBay status patterns and their meanings
STATUS_PATTERNS = {
    r"Bidding ended on": "ENDED (auction expired)",
    r"This listing was ended": "ENDED (seller ended)",
    r"Item sold on": "SOLD",
    r"This listing sold on": "SOLD",
    r"This listing has ended": "ENDED",
    r"no longer available": "ENDED/UNAVAILABLE",
}

# Price patterns
PRICE_PATTERNS = {
    r'class="x-price-primary"[^>]*>([^<]+)': "Primary price element",
    r'itemprop="price"[^>]*content="([^"]+)"': "Schema.org price",
    r'(\$|£|€|C \$|AU \$)\s*[\d,]+\.?\d*': "Currency + amount",
}

# Status message selectors
SELECTOR_PATTERNS = {
    r'class="[^"]*d-statusmessage[^"]*"[^>]*>([^<]*)': "Status message div",
    r'class="[^"]*x-item-title__mainTitle[^"]*"[^>]*>([^<]*)': "Main title",
    r'class="[^"]*ux-layout-section--STATUSCONTAINER[^"]*"': "Status container",
}

def search_file(filepath: str, patterns: dict[str, str], context_chars: int = 100) -> list[tuple[str, str, str]]:
    """Search file for patterns and return matches with context."""
    try:
        content = Path(filepath).read_text(encoding='utf-8', errors='ignore')
    except Exception as e:
        print(f"Error reading file: {e}", file=sys.stderr)
        return []

    matches = []
    for pattern, description in patterns.items():
        for match in re.finditer(pattern, content, re.IGNORECASE):
            start = max(0, match.start() - context_chars)
            end = min(len(content), match.end() + context_chars)
            context = content[start:end].replace('\n', ' ').strip()
            matches.append((description, match.group(), context[:200]))

    return matches

def analyze_listing_status(filepath: str) -> dict:
    """Analyze an HTML file to determine listing status."""
    try:
        content = Path(filepath).read_text(encoding='utf-8', errors='ignore')
    except Exception as e:
        return {"error": str(e)}

    result = {
        "file": filepath,
        "size_kb": Path(filepath).stat().st_size / 1024,
        "detected_status": "UNKNOWN",
        "status_indicators": [],
        "has_title": False,
        "has_price": False,
    }

    # Check for status indicators
    for pattern, meaning in STATUS_PATTERNS.items():
        if re.search(pattern, content, re.IGNORECASE):
            result["status_indicators"].append(f"{meaning}: '{pattern}'")
            if "ENDED" in meaning:
                result["detected_status"] = "ENDED"
            elif "SOLD" in meaning:
                result["detected_status"] = "SOLD"

    # Check for title
    if re.search(r'class="[^"]*x-item-title__mainTitle', content):
        result["has_title"] = True

    # Check for price
    if re.search(r'class="[^"]*x-price-primary', content):
        result["has_price"] = True

    # Default to Active if has title but no status indicators
    if result["detected_status"] == "UNKNOWN" and result["has_title"]:
        result["detected_status"] = "ACTIVE"

    return result

def main():
    parser = argparse.ArgumentParser(description="Search HTML for parser-relevant patterns")
    subparsers = parser.add_subparsers(dest="command", required=True)

    # status command - analyze listing status
    status_parser = subparsers.add_parser("status", help="Analyze listing status from HTML")
    status_parser.add_argument("file", help="HTML file to analyze")

    # search command - search for custom pattern
    search_parser = subparsers.add_parser("search", help="Search for a pattern in HTML")
    search_parser.add_argument("file", help="HTML file to search")
    search_parser.add_argument("pattern", help="Regex pattern to search for")
    search_parser.add_argument("--context", "-c", type=int, default=100, help="Context characters")

    # patterns command - search all known patterns
    patterns_parser = subparsers.add_parser("patterns", help="Search all known patterns")
    patterns_parser.add_argument("file", help="HTML file to search")

    args = parser.parse_args()

    if args.command == "status":
        result = analyze_listing_status(args.file)
        print(f"\nFile: {result['file']}")
        print(f"Size: {result['size_kb']:.1f} KB")
        print(f"Has title: {result['has_title']}")
        print(f"Has price: {result['has_price']}")
        print(f"\nDetected status: {result['detected_status']}")
        if result['status_indicators']:
            print("\nStatus indicators found:")
            for indicator in result['status_indicators']:
                print(f"  - {indicator}")
        else:
            print("\nNo explicit status indicators found")

    elif args.command == "search":
        matches = search_file(args.file, {args.pattern: "Custom pattern"}, args.context)
        if matches:
            print(f"\nFound {len(matches)} match(es):\n")
            for desc, match, context in matches:
                print(f"Match: {match}")
                print(f"Context: ...{context}...")
                print()
        else:
            print("No matches found")

    elif args.command == "patterns":
        print("\n=== STATUS PATTERNS ===")
        for desc, match, context in search_file(args.file, STATUS_PATTERNS):
            print(f"\n{desc}")
            print(f"  Match: {match}")

        print("\n=== PRICE PATTERNS ===")
        for desc, match, context in search_file(args.file, PRICE_PATTERNS):
            print(f"\n{desc}")
            print(f"  Match: {match[:100]}")

        print("\n=== SELECTOR PATTERNS ===")
        for desc, match, context in search_file(args.file, SELECTOR_PATTERNS):
            print(f"\n{desc}")
            print(f"  Match: {match[:100]}")

if __name__ == "__main__":
    main()
