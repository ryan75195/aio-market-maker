"""
Integration tests for the variant labeling prompt.

Runs curated listing pairs through the structured output prompt and checks
that the LLM returns the expected label. Iterating on the prompt until
all cases pass reliably.
"""

import json
import os
import requests
import time
import sys

def _load_api_key():
    key = os.environ.get("OPENAI_API_KEY")
    if key:
        return key
    # Fallback: read from local.settings.json
    settings_path = os.path.join(
        os.path.dirname(__file__),
        "..", "..", "AIOMarketMaker.Api", "bin", "Debug", "net8.0", "local.settings.json",
    )
    try:
        with open(settings_path) as f:
            settings = json.load(f)
        return settings["Values"]["OpenAi:ApiKey"]
    except (FileNotFoundError, KeyError):
        return None


API_KEY = _load_api_key()
MODEL = "gpt-5-mini"
API_URL = "https://api.openai.com/v1/chat/completions"

# ── Structured output schema ─────────────────────────────────────────────
# reasoning FIRST so the model thinks before deciding (autoregressive benefit)
RESPONSE_SCHEMA = {
    "type": "json_schema",
    "json_schema": {
        "name": "variant_classification",
        "strict": True,
        "schema": {
            "type": "object",
            "properties": {
                "reasoning": {
                    "type": "string",
                    "description": "Brief analysis of key similarities and differences between the two listings.",
                },
                "label": {
                    "type": "integer",
                    "enum": [0, 1],
                    "description": "1 if same variant, 0 if different variant.",
                },
                "confidence": {
                    "type": "string",
                    "enum": ["high", "low"],
                    "description": "high if the distinction is clear, low if borderline.",
                },
            },
            "required": ["reasoning", "label", "confidence"],
            "additionalProperties": False,
        },
    },
}

# ── Prompt ────────────────────────────────────────────────────────────────
SYSTEM_PROMPT = """You are classifying whether two eBay listings are the same product variant.

Same variant: identical functional specifications (model, size, storage, capacity, generation) and same level of completeness. Both sold as single items. Color, cosmetic condition, and packaging differences are acceptable.

Different variant: any difference in functional specifications, quantity (single unit vs bundle/lot), or mismatched completeness (e.g. complete product vs accessory-only, parts-only, box-only, or non-functional/for-parts)."""


def build_user_prompt(product_name, title_a, desc_a, title_b, desc_b):
    parts = [f"Product category: {product_name}\n"]
    parts.append(f"Listing A: {title_a}")
    if desc_a:
        parts.append(f"{desc_a}")
    parts.append(f"\nListing B: {title_b}")
    if desc_b:
        parts.append(f"{desc_b}")
    return "\n".join(parts)


# ── Test cases ────────────────────────────────────────────────────────────
# Each case: (name, product, title_a, desc_a, title_b, desc_b, expected_label)
#
# Organized by category to ensure coverage across:
# - Product types (electronics, fashion, luxury, home, collectibles, outdoor)
# - Edge cases (quantity, completeness, generation, size, descriptions)
# - Positive and negative balance (~50/50)
TEST_CASES = [
    # ═══════════════════════════════════════════════════════════════════════
    # SAME VARIANT (expect 1) — ~25 cases
    # ═══════════════════════════════════════════════════════════════════════

    # -- Electronics: same specs, different title style --
    (
        "ps5_slim_disc_same",
        "PlayStation 5 Console",
        "Sony PlayStation 5 Slim Disc Console 1TB, 4K Blu-ray, White",
        None,
        "Sony PlayStation 5 Slim Disc Edition 1TB White New Sealed",
        None,
        1,
    ),
    (
        "macbook_pro_same_specs",
        "MacBook Pro M3",
        "Apple MacBook Pro 14\" (512GB SSD, M3 Pro, 18GB) Laptop - Space Black - MRX33B/A",
        None,
        "Apple MacBook Pro 14\" (512GB SSD, M3 Pro, 18GB, 14C-GPU) Laptop - Space Black",
        None,
        1,
    ),
    (
        "ipad_pro_2021_same_specs",
        "iPad Pro",
        "Apple iPad Pro 2021 3rd Gen M1 11-inch 128GB Wi-Fi Cellular Space Grey",
        None,
        "Apple iPad Pro 11 (2021) 3rd Gen Wi-Fi + Cell 128GB Space Grey",
        None,
        1,
    ),
    (
        "bose_qc45_same_model",
        "Bose QuietComfort Headphones",
        "Bose QuietComfort QC 45 QC45 Noise Cancelling Bluetooth Headphones Black - UK",
        None,
        "Bose QC45 Wireless Noise Cancelling Headphones - Black",
        None,
        1,
    ),
    (
        "dji_mini4_pro_same",
        "DJI Mini Drone",
        "DJI Mini 4 Pro Camera Drone with Remote Controller RC-N2",
        None,
        "DJI Mini 4 Pro Drone + RC-N2 Remote - Excellent Condition",
        None,
        1,
    ),
    (
        "apple_watch_ultra_same",
        "Apple Watch Ultra",
        "Apple Watch Ultra GPS + LTE Cellular 49mm Titanium Case + Starlight Alpine Loop",
        None,
        "Apple Watch Ultra 49mm Titanium GPS + Cellular - Alpine Loop Starlight",
        None,
        1,
    ),

    # -- Fashion: same model/size, different condition --
    (
        "jordan1_same_model_diff_condition",
        "Nike Air Jordan 1",
        "Air Jordan 1 Retro OG High Court Purple VNDS Size 10.5 Nike Air Authentic",
        "Barely been worn, basically new. Stored in pet free/smoke free home.",
        "Air Jordan 1 Retro OG High Court Purple Size 10.5 Used Good Condition",
        "Worn a few times, some minor creasing on toe box.",
        1,
    ),
    (
        "yeezy_350_v2_same_colorway_size",
        "Adidas Yeezy Boost 350",
        "Adidas Yeezy Boost 350 V2 Oreo Core Black Size UK 10",
        "Used in good condition. Comes with original box.",
        "Yeezy Boost 350 V2 Oreo UK10 - Pre-owned",
        None,
        1,
    ),
    (
        "lv_neverfull_mm_same",
        "Louis Vuitton Neverfull",
        "Louis Vuitton Damier Ebene Neverfull MM Red Interior with Dust Bag",
        "Classic rectangular shape, brown exterior, red lining.",
        "Louis Vuitton Neverfull MM Damier Ebene Red Interior - Authentic",
        None,
        1,
    ),

    # -- Luxury watches: same reference --
    (
        "rolex_sub_no_date_same",
        "Rolex Submariner",
        "Rolex Submariner 2022 No Date 41mm Full Set 1 Owner From New",
        "Full set (box+papers), 1 owner from new, well looked after.",
        "Rolex Submariner No Date 41mm 2022 124060 Complete Set",
        None,
        1,
    ),
    (
        "omega_seamaster_chrono_same",
        "Omega Seamaster Watch",
        "Omega Seamaster Chronograph (2018) - 44mm Co-Axial Automatic Watch - Black Dial",
        "Model 212.30.44.50.01.001. November 2018 UK watch sale.",
        "Omega Seamaster Planet Ocean 600M Chrono 44mm 212.30.44.50.01.001",
        None,
        1,
    ),

    # -- Home/Kitchen: same model --
    (
        "le_creuset_round_7qt_same",
        "Le Creuset Dutch Oven",
        "LeCreuset NEW 7.25Qt Signature Round Dutch Oven, Azure Blue",
        "Retails for $425.00.",
        "Le Creuset 7.25 Qt Round Dutch Oven - Azure Blue - Brand New",
        None,
        1,
    ),
    (
        "kitchenaid_classic_same",
        "KitchenAid Stand Mixer",
        "KitchenAid 5K45SSBWH Classic Stand Mixer - White",
        None,
        "KitchenAid Classic Series 4.5 Qt Stand Mixer - White K45SSWH",
        None,
        1,
    ),

    # -- Collectibles: same pressing/set --
    (
        "beatles_abbey_road_same_pressing",
        "Abbey Road Vinyl",
        "THE BEATLES ABBEY ROAD 1969 UK 1st MISALIGNED PCS7088 -2/-1 NO HER MAJESTY VG/VG",
        None,
        "THE BEATLES - ABBEY ROAD 1969 UK 1st VINYL LP PCS7088 -2/-1 NO HER MAJESTY G+/VG",
        None,
        1,
    ),
    (
        "lego_same_set_number",
        "LEGO Star Wars Set",
        "LEGO Star Wars: Obi-Wan Kenobi vs. Darth Vader (75334) Brand New & Sealed",
        None,
        "LEGO 75334 Star Wars Obi Wan vs Darth Vader Set - New Sealed",
        None,
        1,
    ),

    # -- Same level of incompleteness --
    (
        "airpods_pro_2nd_gen_right_earbud",
        "AirPods Pro 2",
        "Apple AirPods Pro 2nd Generation Right Earbud ONLY",
        None,
        "Apple AirPod Pro (2nd Generation) RIGHT ONLY",
        None,
        1,
    ),
    (
        "rtx4090_heatsink_same_model",
        "RTX 4090 Graphics Card",
        "ASUS GeForce RTX 4090 White OC Edition Gaming Graphics Card - Heatsink ONLY",
        None,
        "ASUS ROG Strix NVIDIA GeForce RTX 4090 OC Graphic Card White Heatsink only",
        None,
        1,
    ),

    # -- Refurbished/Open box vs new (same variant) --
    (
        "refurbished_vs_new_same_variant",
        "iPad Pro",
        "Apple iPad Pro 11\" M1 2021 128GB WiFi Space Grey - Refurbished Excellent",
        "Certified refurbished. Fully functional, minor cosmetic marks.",
        "Apple iPad Pro 11\" M1 2021 128GB WiFi Space Grey - Brand New Sealed",
        None,
        1,
    ),

    # -- Junk description should be ignored (same product) --
    (
        "bose_qc45_junk_description",
        "Bose QuietComfort Headphones",
        "Bose QuietComfort QC 45 Noise Cancelling Bluetooth Headphones Black",
        "@media screen and (max-width: 600px) { .pcdesfromibay { display:none; } }",
        "Bose QC45 Wireless Over-Ear Noise Cancelling Headphones Black",
        "Great headphones, barely used. Noise cancelling works perfectly.",
        1,
    ),

    # -- Console with game mention vs same console --
    # Game bundled in title makes this a bundle vs standalone = different.
    # This is correct: for price comparison, bundled items inflate price.
    (
        "ps5_with_game_bundle_vs_console",
        "PlayStation 5 Console",
        "Sony PlayStation 5 Slim Disc Console 1TB, 4K Blu-ray, White With Ghost Of Yotei",
        None,
        "Sony PlayStation 5 Slim Disc Edition 1TB White New Sealed",
        None,
        0,
    ),

    # -- Description-only differentiation confirms same product --
    (
        "dji_mini4_pro_desc_confirms_same",
        "DJI Mini Drone",
        "DJI Mini 4 Pro Fly More Combo (RC2)",
        "DJI Mini 4 Pro with RC2 remote, 3 batteries, carry bag. 4K/60fps HDR.",
        "DJI Mini 4 Pro Fly More Combo RC2 4K HDR Drone",
        None,
        1,
    ),

    # -- Same watch, different listing style --
    (
        "rolex_sub_date_same_ref",
        "Rolex Submariner",
        "ROLEX Submariner Date 116610LN",
        "Rolex Submariner Date, Model 116610LN, manufactured circa 2011.",
        "Rolex Submariner Date Black Dial 40mm 116610LN Automatic",
        None,
        1,
    ),

    # ═══════════════════════════════════════════════════════════════════════
    # DIFFERENT VARIANT (expect 0) — ~25 cases
    # ═══════════════════════════════════════════════════════════════════════

    # -- Electronics: different model/spec --
    (
        "ps5_disc_vs_digital",
        "PlayStation 5 Console",
        "Boxed Sony PlayStation 5 Ps5 Slim Disc Edition Console Perfect Condition",
        None,
        "Sony PlayStation 5 Slim Digital Edition 825GB White Console with Controller",
        None,
        0,
    ),
    (
        "ps5_slim_vs_original",
        "PlayStation 5 Console",
        "Sony PlayStation 5 Slim Disc Console 1TB White",
        None,
        "Sony PlayStation 5 Blu-ray Edition Console White, 825GB, Disc Drive + Controller",
        None,
        0,
    ),
    (
        "macbook_different_storage",
        "MacBook Pro M3",
        "Apple MacBook Pro 14\" (512GB SSD, M3 Pro, 18GB) Laptop - Space Black",
        None,
        "Apple MacBook Pro 14\" (1TB SSD, M3 Pro, 18GB) Laptop - Space Black",
        None,
        0,
    ),
    (
        "ipad_different_generation",
        "iPad Pro",
        "Apple iPad Pro 2021 3rd Gen M1 11-inch 128GB Wi-Fi Space Grey",
        None,
        "Apple iPad Pro 13\" 256GB M5 2025 Space Black (WiFi ONLY)",
        None,
        0,
    ),
    (
        "ipad_wifi_vs_cellular",
        "iPad Pro",
        "Apple iPad Pro 11\" M1 2021 128GB WiFi Only Space Grey",
        None,
        "Apple iPad Pro 11\" M1 2021 128GB WiFi + Cellular Space Grey",
        None,
        0,
    ),
    (
        "rtx4090_different_manufacturer",
        "RTX 4090 Graphics Card",
        "MSI GeForce RTX 4090 SUPRIM X 24GB GDDR6X Graphics Card",
        None,
        "GIGABYTE GeForce RTX 4090 WINDFORCE V2 24GB GDDR6X Graphics Card",
        None,
        0,
    ),

    # -- Fashion: different size/colorway/version --
    (
        "jordan1_different_size",
        "Nike Air Jordan 1",
        "Air Jordan 1 Retro OG High Barons Size 8.5 Used",
        None,
        "Air Jordan 1 Retro OG High Barons Size 10.5 Used",
        None,
        0,
    ),
    (
        "jordan1_high_vs_mid",
        "Nike Air Jordan 1",
        "Air Jordan 1 Retro OG High Court Purple Size 10.5",
        None,
        "Air Jordan 1 Mid Dq8426 142 Size 10.5",
        "Brand new in box Jordan 1 mid DQ8426 142.",
        0,
    ),
    (
        "yeezy_v1_vs_v2",
        "Adidas Yeezy Boost 350",
        "Adidas Yeezy Boost 350 V1 Pirate Black BB5350 UK9",
        "Brand new in box.",
        "Adidas Yeezy Boost 350 V2 Oreo Core Black Size UK9",
        "Used in good condition.",
        0,
    ),
    (
        "yeezy_different_size",
        "Adidas Yeezy Boost 350",
        "adidas Yeezy Boost 350 V2 Low Static Non-Reflective Size 10.5",
        None,
        "adidas Yeezy Boost 350 V2 Low Static Non-Reflective Size 8",
        None,
        0,
    ),

    # -- Luxury: subtle model differences --
    (
        "rolex_sub_date_vs_no_date",
        "Rolex Submariner",
        "Rolex Submariner 2022 No Date 41mm Full Set",
        None,
        "ROLEX Submariner Date 116610LN",
        "Submariner Date, manufactured circa 2011.",
        0,
    ),
    (
        "omega_different_model",
        "Omega Seamaster Watch",
        "OMEGA Seamaster 120m 1999 LIMITED Ed. Auto Chronometer Men's Watch 38mm Black",
        "50th Anniversary limited edition, Reference 2501.51.00.",
        "Omega Seamaster Chronograph (2018) - 44mm Co-Axial Automatic Watch - Black Dial",
        "Model 212.30.44.50.01.001.",
        0,
    ),
    # Pouch-only vs full bag — make the title unambiguous
    (
        "lv_neverfull_mm_vs_pouch_only",
        "Louis Vuitton Neverfull",
        "Louis Vuitton Damier Ebene Neverfull MM Red Interior with Dust Bag & Brand Card",
        "Full size bag, classic rectangular shape, brown exterior, red lining.",
        "Louis Vuitton Monogram Neverfull MM Pochette / Pouch ONLY",
        "Pouch only, no bag included. The removable wristlet that comes inside the Neverfull.",
        0,
    ),

    # -- Watches: different series --
    (
        "apple_watch_ultra_vs_series10",
        "Apple Watch Ultra",
        "Apple Watch Ultra 3 Black With Black Trail Loop M/L",
        "Excellent condition, reset and removed from iCloud.",
        "Apple Watch Series 10 46mm",
        None,
        0,
    ),
    (
        "apple_watch_ultra_1_vs_3",
        "Apple Watch Ultra",
        "Apple Watch Ultra GPS + LTE Cellular 49mm Titanium Case",
        None,
        "Apple Watch Ultra 3 Black With Black Trail Loop M/L",
        "Excellent condition.",
        0,
    ),

    # -- Home: different capacity/size --
    (
        "le_creuset_different_size",
        "Le Creuset Dutch Oven",
        "LE CREUSET RED 2 QT Tomato Dutch Oven Enamel Cast Iron with Lid",
        "2 QT vintage collectible piece from France.",
        "LeCreuset NEW 7.25Qt Signature Round Dutch Oven, Azure Blue",
        "Retails for $425.00.",
        0,
    ),
    (
        "le_creuset_round_vs_oval",
        "Le Creuset Dutch Oven",
        "Le Creuset Oval Casserole Dish Dutch Oven Red 29cm Cast Iron",
        None,
        "Le Creuset 7.25 Qt Round Dutch Oven - Flame Orange",
        None,
        0,
    ),

    # -- Drones: different model --
    (
        "dji_mini4_vs_mini2",
        "DJI Mini Drone",
        "DJI Mini 4 Pro Camera Drone with Remote Controller RC-N2",
        "Top-of-the-line drone, 4K/60fps HDR.",
        "DJI Mini 2 Fly More Bundle 4K Ready-to-Fly Drone",
        "Great little drone, comes with 3 batteries.",
        0,
    ),

    # -- Headphones: different model in same line --
    (
        "bose_qc_vs_qc_ultra",
        "Bose QuietComfort Headphones",
        "Bose QuietComfort QC 45 Noise Cancelling Bluetooth Headphones Black",
        None,
        "Bose QuietComfort Ultra True Wireless Earbuds - Lunar Blue",
        "Premium wireless earbuds, purchased from official Bose website.",
        0,
    ),
    (
        "bose_qc30_vs_qc45",
        "Bose QuietComfort Headphones",
        "Bose QuietControl 30 Neckband QC30 Bluetooth In-Ear Noise Cancelling Headphones",
        "Very good condition, tested and working.",
        "Bose QuietComfort QC 45 QC45 Noise Cancelling Bluetooth Headphones Black",
        None,
        0,
    ),

    # -- Quantity / bundle --
    (
        "star_wars_single_vs_bundle",
        "Vintage Star Wars Figure",
        "Vintage Star Wars Boba Fett Action Figure 1979 Kenner",
        None,
        "Vintage Star Wars Bundle Of 15 Action Figures",
        None,
        0,
    ),
    (
        "lego_set_vs_lot",
        "LEGO Star Wars Set",
        "LEGO Star Wars: Obi-Wan Kenobi vs. Darth Vader (75334) Brand New & Sealed",
        None,
        "Lego Star Wars Set Lot",
        "Lego Star Wars Set Lot. Multiple sets bundled together.",
        0,
    ),
    (
        "pokemon_single_vs_multi",
        "Pokemon Booster Box Sealed",
        "Pokemon Scarlet & Violet 151 Booster Box Sealed",
        None,
        "5x Pokemon Scarlet & Violet 151 Booster Box Sealed Bundle",
        None,
        0,
    ),

    # -- Accessory / incomplete vs complete --
    (
        "rtx4090_heatsink_only_vs_full_card",
        "RTX 4090 Graphics Card",
        "ASUS GeForce RTX 4090 White OC Edition Gaming Graphics Card - Heatsink ONLY",
        None,
        "ASUS ROG Strix GeForce RTX 4090 OC 24GB GDDR6X Graphics Card",
        "Complete card, fully working.",
        0,
    ),
    (
        "lego_no_minifigs_vs_complete",
        "LEGO Star Wars Set",
        "Lego Star Wars Coruscant Guard Gunship 75354 | NO Minifigures",
        "Being sold as 99% complete, no minifigures.",
        "Lego Star Wars Coruscant Guard Gunship 75354 Complete Set with Minifigures",
        None,
        0,
    ),
    (
        "yeti_cooler_vs_accessory",
        "Yeti Tundra Cooler",
        "YETI Tundra 45 Hard Cooler - White",
        None,
        "YETI Latch Kit RED - ROPES ONLY",
        "Yeti Tundra Replacement Handles - Red.",
        0,
    ),

    # -- For parts / broken --
    (
        "kitchenaid_working_vs_parts",
        "KitchenAid Stand Mixer",
        "KitchenAid 5K45SSBWH Classic Stand Mixer - White",
        None,
        "KitchenAid Classic 5K45SS Stand Mixer with Bowl - White (Parts/Not Working)",
        None,
        0,
    ),
    (
        "dyson_complete_vs_spares",
        "Dyson V15 Vacuum",
        "Dyson V15 Detect Absolute Cordless Vacuum Cleaner",
        "Fully working, comes with all attachments.",
        "Dyson V15 Detect Absolute Cordless Vacuum Cleaner- Body Only - Spares / Repairs",
        None,
        0,
    ),
    (
        "omega_working_vs_parts",
        "Omega Seamaster Watch",
        "Omega Seamaster Automatic Watch 38mm",
        "Running perfectly, recently serviced.",
        "Vintage Omega Seamaster Automatic watch not running parts or repair",
        "Watch not running, wont wind, crystal poor condition. As-Is for parts or repair.",
        0,
    ),

    # -- Left vs right / different side --
    (
        "airpods_right_vs_left_earbud",
        "AirPods Pro 2",
        "Apple AirPods Pro 2nd Generation Right Earbud ONLY",
        None,
        "Apple AirPod Pro (2nd Generation) LEFT ONLY",
        None,
        0,
    ),

    # -- Different artist / product sharing a keyword --
    (
        "beatles_abbey_road_vs_george_benson",
        "Abbey Road Vinyl",
        "The Beatles - Abbey Road LP Capitol Records SO-383",
        None,
        "George Benson - The Other Side Of Abbey Road - Vinyl LP - US A&M EX Gatefold",
        None,
        0,
    ),
    (
        "lego_different_sets",
        "LEGO Star Wars Set",
        "LEGO Star Wars: Obi-Wan Kenobi vs. Darth Vader (75334)",
        None,
        "LEGO Star Wars Luke's Landspeeder 75271",
        "Landspeeder build only with two figures.",
        0,
    ),
]


# ── Runner ────────────────────────────────────────────────────────────────
def classify_pair(product, title_a, desc_a, title_b, desc_b, retries=3):
    user_msg = build_user_prompt(product, title_a, desc_a, title_b, desc_b)

    for attempt in range(retries):
        resp = requests.post(
            API_URL,
            headers={"Authorization": f"Bearer {API_KEY}"},
            json={
                "model": MODEL,
                "messages": [
                    {"role": "system", "content": SYSTEM_PROMPT},
                    {"role": "user", "content": user_msg},
                ],
                "response_format": RESPONSE_SCHEMA,
                "max_completion_tokens": 2000,
            },
        )
        if resp.status_code == 429:
            wait = 2 ** (attempt + 1)
            print(f"        Rate limited, waiting {wait}s...")
            time.sleep(wait)
            continue
        resp.raise_for_status()
        content = resp.json()["choices"][0]["message"]["content"]
        return json.loads(content)

    raise Exception("Max retries exceeded (rate limited)")


def run_tests(verbose=True):
    if not API_KEY:
        print("ERROR: Set OPENAI_API_KEY environment variable or place in local.settings.json")
        sys.exit(1)

    passed = 0
    failed = 0
    errors = []

    for case in TEST_CASES:
        name, product, title_a, desc_a, title_b, desc_b, expected = case

        try:
            result = classify_pair(product, title_a, desc_a, title_b, desc_b)
            label = result["label"]
            confidence = result["confidence"]
            reasoning = result["reasoning"]

            ok = label == expected
            if ok:
                passed += 1
                if verbose:
                    print(f"  PASS  {name} (label={label}, conf={confidence})")
            else:
                failed += 1
                errors.append((name, expected, label, confidence, reasoning))
                print(f"  FAIL  {name} (expected={expected}, got={label}, conf={confidence})")
                print(f"        Reasoning: {reasoning[:200]}")

        except Exception as e:
            failed += 1
            errors.append((name, expected, -1, "error", str(e)))
            print(f"  ERROR {name}: {e}")

        time.sleep(0.5)  # rate limit courtesy

    print(f"\n{'='*60}")
    print(f"Results: {passed}/{passed+failed} passed ({passed/(passed+failed)*100:.0f}%)")
    print(f"{'='*60}")

    if errors:
        print(f"\n{len(errors)} failures:")
        for name, expected, got, conf, reasoning in errors:
            print(f"\n  {name}: expected={expected}, got={got}, conf={conf}")
            print(f"  Reasoning: {reasoning[:300]}")

    return passed, failed, errors


if __name__ == "__main__":
    print(f"Model: {MODEL}")
    print(f"Test cases: {len(TEST_CASES)}")
    print(f"System prompt:\n{SYSTEM_PROMPT}\n")
    print(f"{'='*60}")
    run_tests()
