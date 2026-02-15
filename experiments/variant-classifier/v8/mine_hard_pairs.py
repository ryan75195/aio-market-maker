"""
V8 Hard Pair Mining: Extended spec extraction for ALL weak categories.

Builds on v7 by adding extractors for:
- Jewelry: carat, cut shape, metal type, stone, setting style
- Golf clubs: model variant, loft, flex, shaft, handedness, head-only
- Cycling: groupset model number, speed count, Di2/mechanical, brake type
- Luxury bags: size (mini/small/medium/maxi), leather type, hardware color
- Artwork: artwork title (Banksy etc.), format, edition number
- LEGO: set number (4-5 digit)
- Vinyl: pressing type, condition grade, catalog number
- Guitars: guitar model, series/tier, country of origin, year
- Watches: item type (watch vs chain vs fob), maker, mechanism
- Drums: module model (TD-17, TD-50K2, etc.)
- Pushchairs: model version (Fox/Fox2/Fox3/Fox5/Fox Cub)
- Keyboards: model (Q1/K2 etc.), layout (US/UK/ISO/JP), switch type
- Bikes: model tier (AL2/SL5/SLR7), frame size (cm), generation
- Chainsaws: model number (MS261, MSA200), bar length, power source
- Signed shirts: player name, team, season
- Jeans: waist/length (W36 L34), color/wash, made-in country
- Console editions: special edition name
- Backpack volume: liters (35L, 50L, 65L), back system size (S/M, L/XL)

Usage:
    py -3.12 mine_hard_pairs.py                  # full run
    py -3.12 mine_hard_pairs.py --dry-run         # analyze only, don't write CSV
    py -3.12 mine_hard_pairs.py --max-per-cat 500  # cap pairs per category
"""

import argparse
import csv
import re
import sys
import io
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

import pyodbc

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

# ── Config ────────────────────────────────────────────────────────────────
DATA_DIR = Path(__file__).parent
OUTPUT_CSV = DATA_DIR / "hard_pairs_v8.csv"

DB_CONN = (
    "DRIVER={ODBC Driver 17 for SQL Server};"
    "SERVER=(localdb)\\MSSQLLocalDB;"
    "DATABASE=AIOMarketMaker;"
    "Trusted_Connection=yes;"
)

DEFAULT_MAX_HARD_NEG_PER_CAT = 500
DEFAULT_MAX_HARD_POS_PER_CAT = 200
MIN_LISTINGS_PER_GROUP = 2


# ── Spec Extraction ──────────────────────────────────────────────────────

@dataclass
class SpecFingerprint:
    """Normalized spec tokens extracted from a listing title."""
    # Electronics (from v7)
    cpu: Optional[str] = None
    ram_gb: Optional[int] = None
    storage_gb: Optional[int] = None
    screen_inches: Optional[float] = None
    wattage: Optional[int] = None
    voltage: Optional[int] = None
    capacity_mah: Optional[int] = None
    size_label: Optional[str] = None
    generation: Optional[str] = None
    quantity: Optional[str] = None

    # Jewelry (new)
    carat: Optional[str] = None            # "2.5CT", "1.0CT"
    cut_shape: Optional[str] = None        # "Round", "Princess", "Emerald", "Oval"
    metal_type: Optional[str] = None       # "14K-WG", "18K-RG", "925-SS"
    stone_type: Optional[str] = None       # "Moissanite", "Diamond", "Jade"
    setting_style: Optional[str] = None    # "Solitaire", "Halo", "Hidden-Halo"
    jewelry_design: Optional[str] = None   # "Open-Heart", "Bean", "Teardrop", "Love"

    # Golf (new)
    golf_model: Optional[str] = None       # "Stealth2", "Stealth2-Plus", "Stealth2-HD"
    loft_degrees: Optional[float] = None   # 9.0, 10.5, 12.0
    shaft_flex: Optional[str] = None       # "S", "R", "X", "SR", "Senior"
    handedness: Optional[str] = None       # "RH", "LH"
    club_completeness: Optional[str] = None  # "Full", "HeadOnly", "Headcover"

    # Cycling groupset (new)
    groupset_model: Optional[str] = None   # "R8000", "6800", "R8170"
    speed_count: Optional[str] = None      # "11s", "10s", "12s"
    electronic_type: Optional[str] = None  # "Di2", "Mechanical"
    brake_type: Optional[str] = None       # "Rim", "Disc", "Hydraulic"

    # Luxury bags (new)
    bag_size: Optional[str] = None         # "Mini", "Small", "Medium", "Maxi", "Jumbo"
    leather_type: Optional[str] = None     # "Lambskin", "Caviar", "Calfskin", "Patent"
    hardware_color: Optional[str] = None   # "GHW", "SHW"
    flap_type: Optional[str] = None        # "Single", "Double"

    # Artwork (new)
    artwork_name: Optional[str] = None     # specific artwork title
    art_format: Optional[str] = None       # "Canvas", "Lithograph", "Print", "Poster"
    edition_number: Optional[str] = None   # "67/150"

    # LEGO (new)
    lego_set_number: Optional[str] = None  # "75263", "42141"

    # Vinyl (new)
    pressing_type: Optional[str] = None    # "Original", "Reissue", "Japanese", "HalfSpeed"
    vinyl_grade: Optional[str] = None      # "NM", "VG+", "EX"
    catalog_number: Optional[str] = None   # "PCS 7088", "SO-383"

    # Guitars (new)
    guitar_model: Optional[str] = None     # "Stratocaster", "Telecaster", "Les-Paul"
    guitar_series: Optional[str] = None    # "Player", "American-Standard", "Custom-Shop"
    origin_country: Optional[str] = None   # "USA", "Mexico", "Japan"

    # Drums (new)
    drum_module: Optional[str] = None      # "TD-17", "TD-17KV", "TD-50K2"

    # Pushchairs (new)
    pushchair_version: Optional[str] = None  # "Fox", "Fox2", "Fox3", "Fox5", "FoxCub"
    pushchair_type: Optional[str] = None     # "Full", "Parts"

    # Keyboards (new)
    keyboard_model: Optional[str] = None   # "Q1", "K2", "K8", "C3"
    keyboard_layout: Optional[str] = None  # "US", "UK", "ISO", "JP"
    switch_type: Optional[str] = None      # "Red", "Brown", "Blue", "Clear"

    # Bikes (new)
    bike_tier: Optional[str] = None        # "AL2", "SL5", "SLR7"
    frame_size_cm: Optional[int] = None    # 52, 54, 56

    # Chainsaws (new)
    chainsaw_model: Optional[str] = None   # "MS261", "MSA200", "MS172"
    bar_length_inches: Optional[int] = None  # 12, 14, 16, 22
    power_source: Optional[str] = None     # "Petrol", "Battery"

    # Signed shirts (new)
    player_name: Optional[str] = None      # extracted player name
    team_name: Optional[str] = None        # extracted team/country
    season: Optional[str] = None           # "24/25", "2022-23"

    # Jeans (new)
    waist_length: Optional[str] = None     # "W36L34"
    denim_color: Optional[str] = None      # "Black", "Blue", "Cream"
    made_in: Optional[str] = None          # "USA", "UK", "Tunisia"

    # Console editions (new)
    console_edition: Optional[str] = None  # "Pokemon", "Zelda", "Mario", "Splatoon"

    # Backpacks (new)
    pack_volume_l: Optional[int] = None    # 35, 50, 65

    # Remarkable (new)
    remarkable_model: Optional[str] = None  # "2", "Paper-Pro", "Paper-Pro-Move"

    # Whoop (new)
    whoop_version: Optional[str] = None    # "4.0", "3.0"
    whoop_item: Optional[str] = None       # "Strap", "Band", "Charger"

    # Signed shirt item type (new)
    shirt_item_type: Optional[str] = None  # "Shirt", "Frame", "Shorts"

    # Antique watches (new)
    watch_item_type: Optional[str] = None  # "Pocket-Watch", "Fob-Medal", "Albert-Chain"
    watch_mechanism: Optional[str] = None  # "Fusee", "Verge", "Lever", "Quartz"
    watch_working: Optional[str] = None    # "Running", "Not-Working", "Spares"

    # P0: Cross-category product brand (electronics)
    product_brand: Optional[str] = None     # "Nintendo-Switch", "PlayStation-5"

    # P0: Guitar brand (separate from model)
    guitar_brand: Optional[str] = None      # "Fender", "Gibson", "Squier"

    # P0: Diamond presence (jewelry)
    has_diamonds: Optional[str] = None      # "Diamond", "No-Diamond"

    # P0: Ring type (Moissanite)
    ring_type: Optional[str] = None         # "Engagement-Ring", "Mens-Band", "Wedding-Set"

    # P0: Necklace/chain length (Tiffany)
    necklace_length: Optional[str] = None   # "16in", "41in"

    # P0: Roomba model
    roomba_model: Optional[str] = None      # "S9+", "j7", "Max705"

    # P0: Product completeness (iRobot, Whoop)
    product_completeness: Optional[str] = None  # "Robot-Only", "With-Base", "Strap-Only"

    # P1: Bracelet style (Cartier)
    bracelet_style: Optional[str] = None    # "Open-Cuff", "Small-Model"

    # P1: Bracelet size (Cartier)
    bracelet_size: Optional[int] = None     # 16, 17, 18

    # P1: Signature type (Signed shirts)
    signature_type: Optional[str] = None    # "Printed", "Hand-Signed"

    # P1: Framed (Signed shirts)
    framed: Optional[str] = None            # "Framed", "Unframed"

    # P1: Vinyl format
    vinyl_format: Optional[str] = None      # "LP", "7in", "12in"

    # P1: Groupset discipline (cycling)
    groupset_discipline: Optional[str] = None  # "Road", "TT", "Gravel"

    @property
    def has_specs(self) -> bool:
        """Returns True if at least one spec was extracted."""
        for k, v in self.__dict__.items():
            if k == "raw_tokens":
                continue
            if v is not None:
                return True
        return False

    def spec_dict(self) -> dict:
        """Returns only non-None specs as a dict for comparison."""
        d = {}
        for k, v in self.__dict__.items():
            if k == "raw_tokens":
                continue
            if v is not None:
                d[k] = v
        return d

    def diff(self, other: "SpecFingerprint") -> dict:
        """Returns specs that differ between two fingerprints.
        Only compares specs that are present in BOTH fingerprints.
        Applies tolerances for known near-equivalences."""
        diffs = {}
        mine = self.spec_dict()
        theirs = other.spec_dict()
        for key in mine.keys() & theirs.keys():
            a, b = mine[key], theirs[key]
            if a == b:
                continue

            # Screen size tolerance: 16.0 vs 16.2 etc.
            if key == "screen_inches":
                if abs(float(a) - float(b)) <= 0.3:
                    continue

            # Voltage equivalence: DeWalt 20V Max = 18V nominal
            if key == "voltage":
                pair = tuple(sorted([int(a), int(b)]))
                if pair == (18, 20):
                    continue

            # Carat tolerance: 2.0 vs 2.00 (normalize already handles)
            # Loft tolerance: none (9 vs 10.5 is a real difference)

            # Frame size tolerance: 52 vs 54 is a real difference for bikes

            diffs[key] = (a, b)
        return diffs

    raw_tokens: tuple = field(default_factory=tuple)


# ═══════════════════════════════════════════════════════════════════════════
# EXISTING EXTRACTORS (from v7)
# ═══════════════════════════════════════════════════════════════════════════

CPU_PATTERNS = [
    re.compile(r'\b(i[3579])\s*[-]?\s*(\d{4,5}[A-Z]*)\b', re.IGNORECASE),
    re.compile(r'\b(i[3579])\b', re.IGNORECASE),
    re.compile(r'\b(M[1-4])\s*(Pro|Max|Ultra)?\b', re.IGNORECASE),
    re.compile(r'\b(Ryzen\s*\d)\b', re.IGNORECASE),
    re.compile(r'\b(A\d{2})\b', re.IGNORECASE),
    re.compile(r'\b(Snapdragon\s*\d{3,4})\b', re.IGNORECASE),
    re.compile(r'\b((?:RTX|GTX)\s*\d{4}\s*(?:Ti|SUPER)?)\b', re.IGNORECASE),
]

RAM_PATTERN = re.compile(
    r'\b(\d+)\s*GB\s*(?:RAM|DDR\d?|Memory|LPDDR\d?)\b', re.IGNORECASE
)
RAM_FALLBACK = re.compile(
    r'\b(\d+)\s*GB\b(?!\s*(?:SSD|HDD|Storage|eMMC|Flash|Disk|Hard|NVME|NVMe))',
    re.IGNORECASE
)

STORAGE_TB = re.compile(
    r'\b(\d+)\s*TB\s*(?:SSD|HDD|Storage|NVMe|NVME|Hard|Disk)?\b', re.IGNORECASE
)
STORAGE_GB = re.compile(
    r'\b(\d+)\s*GB\s*(?:SSD|HDD|Storage|eMMC|Flash|Disk|Hard|NVMe|NVME)\b',
    re.IGNORECASE
)
STORAGE_COMMON_SIZES = {128, 256, 512}

SCREEN_PATTERN = re.compile(
    r'\b(\d+\.?\d*)\s*(?:"|inch|Inch|INCH|-inch|-Inch)\b', re.IGNORECASE
)

WATTAGE_PATTERN = re.compile(r'\b(\d+)\s*W\b(?!h|att|/|\s*/)', re.IGNORECASE)
VOLTAGE_PATTERN = re.compile(r'\b(\d+)\s*V\b', re.IGNORECASE)
MAH_PATTERN = re.compile(r'\b(\d+)\s*mAh\b', re.IGNORECASE)

SIZE_PATTERN = re.compile(
    r'\b(?:Size\s*)?(UK|US|EU)\s*(\d+\.?\d*)\b', re.IGNORECASE
)
SIZE_LABEL_PATTERN = re.compile(
    r'\b(?:Size\s+)(S|M|L|XS|XL|XXS|XXL|XXXL)\b'
    r'|\b(XXS|XS|XXL|XL|XXXL)\b'
    r'|(?<=[\s,/(-])(S|M|L)(?=[\s,/)\]-]|\Z)',
    re.IGNORECASE
)

GEN_PATTERN = re.compile(
    r'\b(?:Gen(?:eration)?\s*(\d+)|(\d+)(?:st|nd|rd|th)\s*Gen(?:eration)?|'
    r'Mark\s*(\d+)|(?:Series|Version|Rev)\s*(\d+))\b',
    re.IGNORECASE
)

QUANTITY_PATTERN = re.compile(
    r'\b(bundle|lot|pair|set\s*of\s*\d+|\d+\s*pack|[2-9]\d*\s*x\s+|x\s*[2-9]\d*\b)',
    re.IGNORECASE
)

# ═══════════════════════════════════════════════════════════════════════════
# NEW EXTRACTORS (v8)
# ═══════════════════════════════════════════════════════════════════════════

# ── Jewelry ──────────────────────────────────────────────────────────────

# Carat weight: "2.5Ct", "2.50CTW", "1.0 Ct", "3 Carat", "0.77ct"
CARAT_PATTERN = re.compile(
    r'\b(\d+\.?\d*)\s*(?:CTW?|Carat|ct)\b', re.IGNORECASE
)

# Cut shape: Round Cut, Princess Cut, Emerald Cut, Oval Cut, Cushion, Pear, Marquise
CUT_SHAPE_PATTERN = re.compile(
    r'\b(Round|Princess|Emerald|Oval|Cushion|Pear|Marquise|Radiant|Asscher|Heart)\s*(?:Cut)?\b',
    re.IGNORECASE
)

# Metal type: "14k White Gold", "18K Rose Gold", "Sterling Silver", "s925", "Ag925", "750"
METAL_PATTERNS = [
    # Karat gold with color: "14k White Gold", "18K Rose Gold Plated"
    re.compile(r'\b(\d{1,2})[kK]\s*(White|Rose|Yellow|Pink)?\s*Gold\b', re.IGNORECASE),
    # K18/K14 format (Japanese): "K18 PG", "K18WG", "K18 Pink Gold"
    re.compile(r'\bK(\d{1,2})\s*(PG|WG|YG|RG|Pink\s*Gold|White\s*Gold|Rose\s*Gold|Yellow\s*Gold)?\b', re.IGNORECASE),
    # Hallmark: "750" (18K), "585" (14K), "375" (9K)
    re.compile(r'\b(750|585|375)\b'),
    # Sterling silver: "Sterling Silver", "s925", "Ag925", "925", "s/silver"
    re.compile(r'\b(?:Sterling\s*Silver|[sS]925|Ag925|s/silver)\b', re.IGNORECASE),
    # Platinum
    re.compile(r'\b(Platinum|Pt950)\b', re.IGNORECASE),
]

# Setting style: Solitaire, Halo, Hidden Halo, Prong, Bezel, Pave
SETTING_PATTERN = re.compile(
    r'\b(Solitaire|Hidden\s*Halo|Halo|Pave|Bezel|Cathedral|Bypass|Twisted\s*Shank)\b',
    re.IGNORECASE
)

# Jewelry design names (Tiffany, Cartier etc.)
JEWELRY_DESIGN_PATTERNS = [
    # Tiffany Elsa Peretti designs
    re.compile(r'\b(Open\s*Heart|Teardrop|Tear\s*Drop|Bean|Starfish|Butterfly|Cross|Diamonds?\s*by\s*the\s*Yard)\b', re.IGNORECASE),
    # Cartier designs
    re.compile(r'\b(Love\s*Classic|Baby\s*Love|Love\s*Circle|Love\s*Interlocking|Juste\s*un\s*Clou|Trinity)\b', re.IGNORECASE),
]

# Diamond count: "4P Diamond", "6 Diamonds", "1PD", "Pave Diamond"
DIAMOND_COUNT_PATTERN = re.compile(
    r'\b(\d+)\s*P?\s*(?:Diamond|Dia)\b|\bPave\s*Diamond\b', re.IGNORECASE
)

# Jewelry item type: necklace vs ring vs bracelet vs earrings
JEWELRY_ITEM_PATTERN = re.compile(
    r'\b(Necklace|Ring|Bracelet|Bangle|Earrings?|Studs?|Pendant|Chain)\b',
    re.IGNORECASE
)

# ── Golf clubs ───────────────────────────────────────────────────────────

# TaylorMade model variants: Stealth 2, Stealth 2 Plus, Stealth 2 HD
TAYLORMADE_MODEL_PATTERN = re.compile(
    r'\b(?:Stealth\s*2?\s*(Plus\+?|HD)?|STEALTH2?\s*(PLUS\+?|HD)?)\b', re.IGNORECASE
)

# Callaway model variants: Paradym, Paradym X, Paradym Triple Diamond
CALLAWAY_MODEL_PATTERN = re.compile(
    r'\b(?:Paradym)\s*(X|Triple\s*Diamond|TD|Ai\s*Smoke)?\b', re.IGNORECASE
)

# Loft degrees: "10.5°", "9 Degree", "9°", "12-deg", "8.0 Degrees"
LOFT_PATTERN = re.compile(
    r'\b(\d+\.?\d*)\s*(?:°|[Dd]eg(?:ree)?s?)\b'
)

# Shaft flex: Stiff, Regular, Senior, X-Stiff, SR
FLEX_PATTERN = re.compile(
    r'\b(X[-\s]?Stiff|Stiff|Regular|Senior|SR|[SRXA])\s*(?:Flex)?\b',
    re.IGNORECASE
)

# Handedness: Right-Handed, Left Handed, RH, LH, Lefty
HANDEDNESS_PATTERN = re.compile(
    r'\b(Right[-\s]?Handed?|Left[-\s]?Handed?|Lefty|RH|LH)\b', re.IGNORECASE
)

# Club completeness: Head Only, Headcover, Driver (full club)
CLUB_COMPLETENESS_PATTERN = re.compile(
    r'\b(Head\s*Only|Head\s*cover|Head\s*Onl)\b', re.IGNORECASE
)

# ── Cycling groupsets ────────────────────────────────────────────────────

# Shimano model numbers: R8000, R8070, R8150, R8170, 6800, 6700, 6600, 6400
GROUPSET_MODEL_PATTERN = re.compile(
    r'\b(R\d{4}|[0-9]{4})\b(?=.*(?:Ultegra|Dura.?Ace|105|GRX|XT|Speed|Groupset|Di2|Brake))'
    r'|\b(?:Ultegra|Dura.?Ace|105|GRX|Groupset)\s*(R?\d{4})\b',
    re.IGNORECASE
)

# Speed count: "11-Speed", "10 Speed", "2x11", "11s"
SPEED_COUNT_PATTERN = re.compile(
    r'\b(\d{1,2})[-\s]?[Ss]peed\b|\b(\d)x(\d{1,2})\b|\b(\d{1,2})s\b'
)

# Electronic vs Mechanical: "Di2" (electronic)
ELECTRONIC_PATTERN = re.compile(r'\bDi2\b', re.IGNORECASE)

# Brake type: Rim Brake, Disc Brake, Hydraulic, DB (disc brake)
BRAKE_TYPE_PATTERN = re.compile(
    r'\b(Rim\s*Brake|Disc\s*Brake|Hydraulic|DB)\b', re.IGNORECASE
)

# ── Luxury bags ──────────────────────────────────────────────────────────

# Size: Mini, Small, Medium, Maxi, Jumbo
BAG_SIZE_PATTERN = re.compile(
    r'\b(Mini\s*Rectangular|Mini|Small|Medium|Maxi|Jumbo)\b', re.IGNORECASE
)

# Leather: Lambskin, Caviar, Calfskin, Patent, Nylon
LEATHER_PATTERN = re.compile(
    r'\b(Lambskin|Caviar|Calfskin|Patent|Nylon|Canvas|Tweed)\b', re.IGNORECASE
)

# Hardware: GHW (gold hardware), SHW (silver hardware)
HARDWARE_PATTERN = re.compile(
    r'\b(GHW|SHW|Gold\s*Hardware|Silver\s*[Hh]ardware)\b', re.IGNORECASE
)

# Flap type: Single Flap, Double Flap
FLAP_PATTERN = re.compile(r'\b(Single|Double)\s*Flap\b', re.IGNORECASE)

# ── Artwork ──────────────────────────────────────────────────────────────

# Known Banksy artwork names (most common)
BANKSY_ARTWORKS = [
    "Game Changer", "Girl with Balloon", "Flower Thrower", "Kissing Coppers",
    "Monkey Parliament", "Chimps in Parliament", "Monkeys in Parliament",
    "Di-Faced Tenner", "Love is in the Air", "Pulp Fiction", "Rage",
    "RAINBOW MONKEY", "Steve Jobs", "Super Mario", "Mona Lisa",
    "Orca", "Old Skool", "Fast Food Caveman", "Graffiti Rat",
    "Fallen Angel", "Capitalism", "Stock Market",
]
BANKSY_ARTWORK_PATTERN = re.compile(
    r'\b(' + '|'.join(re.escape(a) for a in BANKSY_ARTWORKS) + r')\b',
    re.IGNORECASE
)

# Art format: Canvas, Lithograph, Print, Poster
ART_FORMAT_PATTERN = re.compile(
    r'\b(Lithograph|Canvas\s*Print|Canvas|Art\s*Print|Poster|Giclee)\b',
    re.IGNORECASE
)

# Edition number: "# 67/150", "Limited", "67/150", "Ed. 5/50"
EDITION_PATTERN = re.compile(
    r'#?\s*(\d+)\s*/\s*(\d+)', re.IGNORECASE
)

# ── LEGO ─────────────────────────────────────────────────────────────────

# Set number: 4-6 digit numbers near LEGO context
# Matches: "LEGO ... Set 75263", "LEGO 75254", "Set 42141", "set 75324", "(42068)"
LEGO_SET_PATTERN = re.compile(
    r'\b(?:LEGO|Lego)\b.*?\b(?:Set\s*#?\s*)?(\d{4,6})\b'
    r'|\b(\d{4,6})\b.*?\b(?:LEGO|Lego)\b'
    r'|\b[Ss]et\s*#?\s*(\d{4,6})\b',
    re.IGNORECASE
)

# ── Vinyl / Records ─────────────────────────────────────────────────────

# Pressing type
PRESSING_PATTERN = re.compile(
    r'\b(First\s*Press(?:ing)?|Original\s*Press(?:ing)?|Reissue|Remaster(?:ed)?|'
    r'Japanese\s*Press(?:ing)?|Half\s*Speed\s*(?:Master)?|MFSL|Anniversary\s*Ed(?:ition)?|'
    r'180\s*[gG](?:ram)?)\b',
    re.IGNORECASE
)

# Vinyl condition grade
VINYL_GRADE_PATTERN = re.compile(
    r'\b(NM|EX|VG\+?|G\+?|Fair|Poor|Mint|SEALED)\b'
    r'|(?:^|\s)(NM/EX|VG/G\+|EX/VG\+|NM/NM|EX/EX)\b',
    re.IGNORECASE
)

# Catalog number patterns: "PCS 7088", "SO-383", common label formats
CATALOG_PATTERN = re.compile(
    r'\b(PCS\s*\d{4}|SO[-\s]?\d{3,4}|B[-\s]?\d{7}[-\s]?\d{2})\b',
    re.IGNORECASE
)

# ── Guitars ──────────────────────────────────────────────────────────────

# Guitar model type
GUITAR_MODEL_PATTERN = re.compile(
    r'\b(Stratocaster|Strat(?:o)?|Telecaster|Tele|Jaguar|Jazzmaster|Mustang|'
    r'Les\s*Paul|SG\s*Standard|Explorer|Flying\s*V|ES[-\s]?\d{3})\b',
    re.IGNORECASE
)

# Guitar series/tier
GUITAR_SERIES_PATTERN = re.compile(
    r'\b(Custom\s*Shop|American\s*(?:Standard|Professional|Ultra|Vintage)|'
    r'Player\s*(?:II|2)?|Standard|Professional|Vintera|Highway\s*One|'
    r'(?:Squier|Squire)\s*(?:by\s*Fender)?)\b',
    re.IGNORECASE
)

# Country of origin (guitars)
GUITAR_ORIGIN_PATTERN = re.compile(
    r'\b(?:Made\s*[Ii]n\s*)?(USA|Mexico|Japan|MIJ|MIM|MIA)\b', re.IGNORECASE
)

# ── Drums ────────────────────────────────────────────────────────────────

# Roland TD module: TD-17, TD-17KV, TD-17KVX, TD-50K2, TD-02K, TD-1DMK
DRUM_MODULE_PATTERN = re.compile(
    r'\b(TD[-\s]?\d{1,2}(?:[A-Z]{0,4}\d?)?)\b', re.IGNORECASE
)

# ── Pushchairs ───────────────────────────────────────────────────────────

# Bugaboo Fox version: Fox, Fox2, Fox 2, Fox3, Fox 3, Fox 5, Fox Cub
BUGABOO_VERSION_PATTERN = re.compile(
    r'\bFox\s*(Cub|5|3|2)?\b', re.IGNORECASE
)

# Pushchair item type: full pushchair vs parts
PUSHCHAIR_PARTS_PATTERN = re.compile(
    r'\b(Wheel\s*Caps?|Trims?|Hood|Canopy|Footmuff|Rain\s*cover|Brake|Chassis|'
    r'Adapter|Bumper\s*Bar|Cup\s*Holder)\b',
    re.IGNORECASE
)

# ── Keyboards ────────────────────────────────────────────────────────────

# Keychron model: Q1, Q2, Q3, Q15, K1, K2, K3, K4, K6, K8, C2, C3, V3
KEYCHRON_MODEL_PATTERN = re.compile(
    r'\b(?:Keychron\s+)([QKVC]\d{1,2})\b', re.IGNORECASE
)

# Keyboard layout
KEYBOARD_LAYOUT_PATTERN = re.compile(
    r'\b(US|UK|JP|ISO\s*(?:UK)?|ANSI)\s*(?:Layout)?\b', re.IGNORECASE
)

# Switch type: Red, Brown, Blue, Clear, Gateron Brown etc.
SWITCH_PATTERN = re.compile(
    r'\b(?:Gateron\s*)?(Red|Brown|Blue|Clear|Black|Silver|Linear|Tactile)\s*(?:Switch(?:es)?)?\b',
    re.IGNORECASE
)

# ── Bikes ────────────────────────────────────────────────────────────────

# Trek Domane tier: AL2, AL3, AL4, AL5, SL5, SL6, SL7, SLR6, SLR7
BIKE_TIER_PATTERN = re.compile(
    r'\b(AL\s*[2-5]|SL\s*[5-7]|SLR\s*[6-9])\b', re.IGNORECASE
)

# Frame size in cm: "54cm", "52cm", "56cm"
FRAME_SIZE_PATTERN = re.compile(
    r'\b(\d{2})\s*cm\b', re.IGNORECASE
)

# ── Chainsaws ────────────────────────────────────────────────────────────

# Stihl model: MS261, MS172, MSA200, MS400C, HT135
STIHL_MODEL_PATTERN = re.compile(
    r'\b(MS[A]?\s*\d{2,3}[A-Z]?|HT\s*\d{3})\b', re.IGNORECASE
)

# Bar length: 12", 14", 16", 22"
BAR_LENGTH_PATTERN = re.compile(
    r'\b(\d{2})\s*(?:"|inch|Inch)\s*(?:Bar|Guide\s*Bar|Chain)?\b', re.IGNORECASE
)

# Power source: Petrol, Battery, Electric
POWER_SOURCE_PATTERN = re.compile(
    r'\b(Petrol|Battery|Electric|Cordless)\b', re.IGNORECASE
)

# ── Signed shirts ────────────────────────────────────────────────────────

# Season format: "24/25", "25/26", "2022-23", "2022/23"
SEASON_PATTERN = re.compile(
    r'\b(20\d{2}[-/]\d{2,4}|\d{2}/\d{2})\b'
)

# Team names (major football clubs + national teams)
FOOTBALL_TEAMS = [
    "Manchester United", "Manchester City", "Liverpool", "Chelsea", "Arsenal",
    "Tottenham", "Everton", "Aston Villa", "Newcastle", "West Ham",
    "Leicester", "Wolves", "Southampton", "Crystal Palace", "Brighton",
    "Sheffield United", "Burnley", "Fulham", "Blackpool", "Millwall",
    "Huddersfield", "England", "Italy", "France", "Germany", "Spain",
    "Barcelona", "Real Madrid", "Juventus", "Bayern Munich", "PSG",
]
TEAM_PATTERN = re.compile(
    r'\b(' + '|'.join(re.escape(t) for t in FOOTBALL_TEAMS) + r')\b',
    re.IGNORECASE
)

# ── Jeans ────────────────────────────────────────────────────────────────

# Waist x Length: "W36 L34", "W28 L32", "34 32", "29x25"
JEANS_SIZE_PATTERN = re.compile(
    r'\bW\s*(\d{2,3})\s*(?:L|x)\s*(\d{2,3})\b'
    r'|\b(\d{2})\s*[xX]\s*(\d{2})\b',
    re.IGNORECASE
)

# Denim color/wash
DENIM_COLOR_PATTERN = re.compile(
    r'\b(Black|Blue|Light\s*Blue|Dark\s*Blue|Indigo|Cream|Stone|Beige|Grey|'
    r'Stonewash(?:ed)?|Faded|Distressed|White)\b',
    re.IGNORECASE
)

# Made in country
MADE_IN_PATTERN = re.compile(
    r'\bMade\s*[Ii]n\s*(USA|UK|Tunisia|Mexico|Japan|China|Turkey)\b',
    re.IGNORECASE
)

# ── Console editions ─────────────────────────────────────────────────────

# Console special editions — NOT including "OLED" (that's the base model)
CONSOLE_EDITION_PATTERN = re.compile(
    r'\b(Pokemon|Pikachu|Zelda|Mario|Splatoon|Animal\s*Crossing|Fortnite|'
    r'Scarlet\s*and\s*Violet|Tears\s*of\s*the\s*Kingdom)\s*(?:Edition|Ed\.?)?\b',
    re.IGNORECASE
)

# ── Backpacks ────────────────────────────────────────────────────────────

# Volume in liters: "35L", "Atmos 50", "AG 65", "65 Litre"
# For backpacks, also match bare numbers after known model names
PACK_VOLUME_PATTERN = re.compile(
    r'\b(\d{2,3})\s*(?:L(?:itre)?|l)\b'
    r'|\b(?:Atmos|AG|AG\s*LT)\s+(\d{2,3})\b',
    re.IGNORECASE
)

# ── reMarkable ───────────────────────────────────────────────────────────

REMARKABLE_MODEL_PATTERN = re.compile(
    r'\b(?:re[Mm]arkable|Remarkable)\s*(Paper\s*Pro\s*Move|Paper\s*Pro|2)\b',
    re.IGNORECASE
)

# ── Whoop ────────────────────────────────────────────────────────────────

WHOOP_VERSION_PATTERN = re.compile(
    r'\bWhoop\s*(\d+\.?\d*)\b', re.IGNORECASE
)

# ── Antique watches ──────────────────────────────────────────────────────

# Watch item type: actual watch vs chain vs fob medal vs accessories
# Order matters: more specific items first so "Fob Medal" beats "Pocket Watch"
WATCH_ITEM_PATTERN = re.compile(
    r'\b(Fob\s*Medal|Albert(?:ina)?\s*(?:Chain)?|Watch\s*Chain|'
    r'Fob\s*Watch|Pocket\s*Watch|Wrist\s*Watch)\b',
    re.IGNORECASE
)

# Watch mechanism: Fusee, Verge, Lever, Key Wind, Crown Wind
WATCH_MECHANISM_PATTERN = re.compile(
    r'\b(Fusee|Verge|Lever|Key\s*Wind|Crown\s*Wind(?:ing)?|Chain\s*Drive|'
    r'Quartz|Automatic|Manual\s*Wind)\b',
    re.IGNORECASE
)

# Watch working status: running, ticks, not working, spares
WATCH_WORKING_PATTERN = re.compile(
    r'\b(Running|Fully\s*Running|ticks|Not\s*Working|Spares|Untested|W/O|Needs\s*Repair)\b',
    re.IGNORECASE
)


# ── P0: Cross-category product brand ────────────────────────────────────
# No category gating — these are specific enough brand+product combos
PRODUCT_BRAND_PATTERNS = [
    (re.compile(r'\bNintendo\s*Switch\b', re.IGNORECASE), "Nintendo-Switch"),
    (re.compile(r'\b(?:PlayStation|PS)\s*5\b', re.IGNORECASE), "PlayStation-5"),
    (re.compile(r'\b(?:PlayStation|PS)\s*4\b', re.IGNORECASE), "PlayStation-4"),
    (re.compile(r'\biPad\s*(?:Pro|Air|Mini)?\b', re.IGNORECASE), "Apple-iPad"),
    (re.compile(r'\bApple\s*Watch\b', re.IGNORECASE), "Apple-Watch"),
    (re.compile(r'\bSonos\s*(?:Arc|One|Beam|Sub|Era)\b', re.IGNORECASE), "Sonos"),
    (re.compile(r'\bSamsung\b.*?\b(?:QLED|Neo|QN\d{2})\b', re.IGNORECASE), "Samsung-TV"),
    (re.compile(r'\bLG\b.*?\bOLED\b', re.IGNORECASE), "LG-TV"),
    (re.compile(r'\bZenbook\b', re.IGNORECASE), "ASUS-Zenbook"),
    (re.compile(r'\bROG\s*Strix\b', re.IGNORECASE), "ASUS-ROG"),
    (re.compile(r'\bRoomba\b', re.IGNORECASE), "iRobot-Roomba"),
]

# ── P0: Guitar brand ──────────────────────────────────────────────────
GUITAR_BRAND_PATTERN = re.compile(
    r'\b(Fender|Gibson|Squier|Epiphone|PRS|Ibanez|Yamaha)\b', re.IGNORECASE
)

# ── P0: Diamond presence (jewelry) ────────────────────────────────────
HAS_DIAMONDS_PATTERN = re.compile(
    r'\b(?:\d+\s*)?[Dd]iamonds?\b', re.IGNORECASE
)

# ── P0: Ring type ──────────────────────────────────────────────────────
RING_TYPE_PATTERN = re.compile(
    r'\b(Mens?\s*(?:Wedding\s*)?Band|Engagement\s*Ring|Wedding\s*(?:Ring\s*)?Set|'
    r'Eternity\s*(?:Ring|Band)|Anniversary\s*Band|Cocktail\s*Ring|'
    r'Signet\s*Ring|Promise\s*Ring)\b',
    re.IGNORECASE
)

# ── P0: Necklace/chain length ─────────────────────────────────────────
NECKLACE_LENGTH_PATTERN = re.compile(
    r'\b(\d+\.?\d*)\s*(?:"|[Ii]nch(?:es)?)\b'
)

# ── P0: Roomba model ──────────────────────────────────────────────────
ROOMBA_MODEL_PATTERN = re.compile(
    r'\bRoomba\s*((?:Combo\s*)?[SsJj]\d+\+?|Max\s*\d{3}|\d{3})(?=\s|$|[,;)\]])',
    re.IGNORECASE
)

# ── P0: Product completeness (iRobot) ─────────────────────────────────
IROBOT_COMPLETENESS_PATTERN = re.compile(
    r'\b(Robot\s*Only|No\s*(?:Charger|Base|Dock)|Clean\s*Base\s*Only|'
    r'Base\s*Only|Dock\s*Only)\b',
    re.IGNORECASE
)

# ── P0: Whoop product type ────────────────────────────────────────────
WHOOP_PRODUCT_PATTERN = re.compile(
    r'\b((?:Fitness|Health|Activity)\s*Tracker|Sensor|Charger|'
    r'Replacement\s*(?:Strap|Band)|(?:Strap|Band)\s*Only)\b',
    re.IGNORECASE
)

# ── P1: Bracelet style (Cartier) ──────────────────────────────────────
BRACELET_STYLE_PATTERN = re.compile(
    r'\b(Open\s*Cuff|Small\s*Model|Interlocking(?:\s*Loop)?|Bangle|'
    r'Juste\s*un\s*Clou|Classic)\b',
    re.IGNORECASE
)

# ── P1: Bracelet size ─────────────────────────────────────────────────
BRACELET_SIZE_PATTERN = re.compile(
    r'\bSize\s*(\d{2})\b', re.IGNORECASE
)

# ── P1: Signature type (Signed shirts) ────────────────────────────────
SIGNATURE_TYPE_PATTERN = re.compile(
    r'\b(Printed\s*Sign(?:ed)?|Print(?:ed)?\s*Autograph|'
    r'Auto(?:graph)?(?:ed)?|Hand[-\s]?Sign(?:ed)?)\b',
    re.IGNORECASE
)

# ── P1: Framed ────────────────────────────────────────────────────────
FRAMED_PATTERN = re.compile(
    r'\b(Framed|Mounted|Display\s*(?:Frame|Case))\b', re.IGNORECASE
)

# ── P1: Vinyl format ──────────────────────────────────────────────────
VINYL_FORMAT_PATTERN = re.compile(
    r'\b(7"\s*(?:Vinyl|Single)?|12"\s*(?:Vinyl|Single)?|'
    r'(?:Double|2\s*x|2x)\s*LP|(?:Triple|3\s*x|3x)\s*LP|'
    r'(?:Vinyl\s*)?LP|LP\s*(?:Vinyl|Album))\b',
    re.IGNORECASE
)

# ── P1: Groupset discipline ───────────────────────────────────────────
GROUPSET_DISCIPLINE_PATTERN = re.compile(
    r'\b(TT|Triathlon|Time\s*Trial|Road|Gravel|CX|Cyclocross)\b',
    re.IGNORECASE
)


# ═══════════════════════════════════════════════════════════════════════════
# CATEGORY-AWARE EXTRACTION
# ═══════════════════════════════════════════════════════════════════════════

# Categories that should skip CPU extraction (model numbers look like CPUs)
CPU_SKIP_CATEGORIES = {
    "iRobot Roomba", "Shark Navigator Vacuum", "Dyson V15 Vacuum",
    "Dyson Airwrap",
}

# Category groups for targeted extraction
JEWELRY_CATEGORIES = {
    "Moissanite Engagement Ring", "Cartier Love Bracelet",
    "Tiffany Elsa Peretti Necklace", "Pandora Charm Bracelet",
}
GOLF_CATEGORIES = {
    "TaylorMade Stealth 2 Driver", "Callaway Paradym Driver",
    # Titleist Pro V1 excluded — golf balls don't have loft/flex/shaft specs
}
CYCLING_CATEGORIES = {
    "Shimano Ultegra Groupset", "Specialized Tarmac Road Bike",
    "Trek Domane Road Bike", "Brompton Folding Bike",
}
BAG_CATEGORIES = {
    "Chanel Classic Flap Bag", "Louis Vuitton Neverfull",
    "Hermes Birkin Bag",
}
ART_CATEGORIES = {"Banksy Print"}
LEGO_CATEGORIES = {"LEGO Star Wars Set", "LEGO Technic Set"}
VINYL_CATEGORIES = {"Abby Road Vinyl"}
GUITAR_CATEGORIES = {
    "Fender Stratocaster Guitar", "Gibson Les Paul Standard",
    "Fender Blues Junior Amplifier",
}
DRUM_CATEGORIES = {"Roland TD-17 Electronic Drums"}
PUSHCHAIR_CATEGORIES = {"Bugaboo Fox Pushchair"}
KEYBOARD_CATEGORIES = {"Keychron Q1 Keyboard"}
BIKE_CATEGORIES = {"Trek Domane Road Bike", "Specialized Tarmac Road Bike"}
CHAINSAW_CATEGORIES = {"Stihl Chainsaw"}
SHIRT_CATEGORIES = {"Signed Football Shirt"}
JEANS_CATEGORIES = {"Vintage Levis 501 Jeans"}
CONSOLE_CATEGORIES = {"Nintendo Switch OLED", "PlayStation 5 Console"}
BACKPACK_CATEGORIES = {"Osprey Atmos Backpack"}
REMARKABLE_CATEGORIES = {"Remarkable 2 Tablet"}
WHOOP_CATEGORIES = {"Whoop 4.0 Strap"}
ROOMBA_CATEGORIES = {"iRobot Roomba"}
WATCH_CATEGORIES = {"Antique Silver Pocket Watch", "Omega Seamaster Watch",
                     "Rolex Submariner", "Vintage Omega Speedmaster",
                     "Apple Watch Ultra", "Garmin Fenix 7 Watch"}


def _normalize_quantity(raw: str) -> str:
    raw = raw.lower().strip()
    if raw == "pair":
        return "2x"
    m = re.match(r'^(\d+)\s*x\s*$', raw)
    if m:
        return f"{m.group(1)}x"
    m = re.match(r'^x\s*(\d+)$', raw)
    if m:
        return f"{m.group(1)}x"
    m = re.match(r'^(\d+)\s*pack$', raw)
    if m:
        return f"{m.group(1)}pack"
    m = re.match(r'^set\s*of\s*(\d+)$', raw)
    if m:
        return f"setof{m.group(1)}"
    return raw


def _normalize_metal(title: str) -> Optional[str]:
    """Extract and normalize metal type to canonical form."""
    for pat in METAL_PATTERNS:
        m = pat.search(title)
        if m:
            text = m.group(0).strip()
            # Normalize karat gold
            karat_m = re.match(r'(\d{1,2})[kK]\s*(White|Rose|Yellow|Pink)?\s*Gold', text, re.IGNORECASE)
            if karat_m:
                k = karat_m.group(1)
                color_raw = (karat_m.group(2) or "Yellow").upper()
                # Normalize Pink -> Rose
                color_map = {"W": "WG", "R": "RG", "Y": "YG", "P": "RG"}
                color = color_map.get(color_raw[0], "YG")
                return f"{k}K-{color}"

            k18_m = re.match(r'K(\d{1,2})\s*(PG|WG|YG|RG|Pink\s*Gold|White\s*Gold|Rose\s*Gold|Yellow\s*Gold)?', text, re.IGNORECASE)
            if k18_m:
                k = k18_m.group(1)
                color = (k18_m.group(2) or "YG").strip().upper()
                # Normalize full names and PG
                color_map = {
                    "PG": "RG", "WG": "WG", "YG": "YG", "RG": "RG",
                }
                if "PINK" in color or "ROSE" in color:
                    color = "RG"
                elif "WHITE" in color:
                    color = "WG"
                elif "YELLOW" in color:
                    color = "YG"
                else:
                    color = color_map.get(color, "YG")
                return f"{k}K-{color}"

            if re.match(r'750', text):
                return "18K"
            if re.match(r'585', text):
                return "14K"
            if re.match(r'375', text):
                return "9K"

            if re.search(r'Sterling|[sS]925|Ag925|s/silver', text, re.IGNORECASE):
                return "925-SS"

            if re.search(r'Platinum|Pt950', text, re.IGNORECASE):
                return "PT"

            return text.upper()
    return None


def _normalize_flex(raw: str) -> str:
    """Normalize shaft flex to single letter."""
    raw = raw.strip().upper()
    if "X" in raw and "STIFF" in raw:
        return "X"
    if raw in ("STIFF", "S"):
        return "S"
    if raw in ("REGULAR", "R"):
        return "R"
    if raw in ("SENIOR",):
        return "A"  # senior flex = A flex
    if raw == "SR":
        return "SR"
    return raw


def _normalize_handedness(raw: str) -> str:
    raw = raw.strip().upper()
    if any(x in raw for x in ("LEFT", "LEFTY", "LH")):
        return "LH"
    return "RH"


def extract_fingerprint(title: str, category: str = "") -> SpecFingerprint:
    """Extract spec fingerprint from an eBay listing title.
    Category is used to enable/disable extractors contextually."""
    if not title:
        return SpecFingerprint()

    fp = SpecFingerprint()
    tokens = []

    # ── Electronics (from v7) ────────────────────────────────────────
    skip_cpu = category in CPU_SKIP_CATEGORIES or bool(
        re.search(r'Roomba|Vacuum|Cleaner|Brush|Filter', title, re.IGNORECASE)
    )

    if not skip_cpu:
        for pattern in CPU_PATTERNS:
            m = pattern.search(title)
            if m:
                cpu_str = re.sub(r'\s+', '', m.group(0).strip().upper())
                fp.cpu = cpu_str
                tokens.append(f"cpu:{cpu_str}")
                break

    # Storage
    m = STORAGE_TB.search(title)
    if m:
        fp.storage_gb = int(m.group(1)) * 1024
        tokens.append(f"storage:{m.group(1)}TB")
    else:
        m = STORAGE_GB.search(title)
        if m:
            fp.storage_gb = int(m.group(1))
            tokens.append(f"storage:{fp.storage_gb}GB")
        else:
            all_gb = re.findall(r'\b(\d+)\s*GB\b', title, re.IGNORECASE)
            for val_str in all_gb:
                val = int(val_str)
                if val in STORAGE_COMMON_SIZES:
                    fp.storage_gb = val
                    tokens.append(f"storage:{val}GB")
                    break

    # RAM
    m = RAM_PATTERN.search(title)
    if m:
        fp.ram_gb = int(m.group(1))
        tokens.append(f"ram:{fp.ram_gb}GB")
    else:
        ram_sizes = {2, 4, 6, 8, 12, 16, 24, 32, 48, 64, 128}
        for val_str in RAM_FALLBACK.findall(title):
            val = int(val_str)
            if fp.storage_gb and val == fp.storage_gb:
                continue
            if val in ram_sizes:
                fp.ram_gb = val
                tokens.append(f"ram:{val}GB")
                break

    # Screen — skip for jewelry categories (inch values are chain lengths)
    if category not in JEWELRY_CATEGORIES:
        m = SCREEN_PATTERN.search(title)
        if m:
            fp.screen_inches = float(m.group(1))
            tokens.append(f"screen:{fp.screen_inches}in")

    # Wattage
    m = WATTAGE_PATTERN.search(title)
    if m:
        watts = int(m.group(1))
        if watts >= 5:
            fp.wattage = watts
            tokens.append(f"watts:{fp.wattage}W")

    # Voltage
    m = VOLTAGE_PATTERN.search(title)
    if m:
        fp.voltage = int(m.group(1))
        tokens.append(f"volts:{fp.voltage}V")

    # Battery
    m = MAH_PATTERN.search(title)
    if m:
        fp.capacity_mah = int(m.group(1))
        tokens.append(f"battery:{fp.capacity_mah}mAh")

    # Size (clothing/shoes)
    m = SIZE_PATTERN.search(title)
    if m:
        region = m.group(1).upper()
        num = m.group(2)
        fp.size_label = f"{region} {num}"
        tokens.append(f"size:{fp.size_label}")
    else:
        m = SIZE_LABEL_PATTERN.search(title)
        if m:
            label = next(g for g in m.groups() if g is not None)
            fp.size_label = label.upper().strip()
            tokens.append(f"size:{fp.size_label}")

    # Generation
    m = GEN_PATTERN.search(title)
    if m:
        gen_num = next(g for g in m.groups() if g is not None)
        fp.generation = f"Gen{gen_num}"
        tokens.append(f"gen:{fp.generation}")

    # Quantity
    m = QUANTITY_PATTERN.search(title)
    if m:
        fp.quantity = _normalize_quantity(m.group(0))
        tokens.append(f"qty:{fp.quantity}")

    # ── P0: Product brand (cross-category disambiguation) ──────────
    # No category gating — these are specific enough brand+product combos
    for brand_pat, brand_name in PRODUCT_BRAND_PATTERNS:
        m = brand_pat.search(title)
        if m:
            fp.product_brand = brand_name
            tokens.append(f"brand:{brand_name}")
            break

    # ── Jewelry ──────────────────────────────────────────────────────
    if category in JEWELRY_CATEGORIES or any(
        w in title.lower() for w in ["moissanite", "cartier", "tiffany", "diamond ring", "engagement ring"]
    ):
        m = CARAT_PATTERN.search(title)
        if m:
            ct = f"{float(m.group(1)):.1f}CT"
            fp.carat = ct
            tokens.append(f"carat:{ct}")

        m = CUT_SHAPE_PATTERN.search(title)
        if m:
            fp.cut_shape = m.group(1).title()
            tokens.append(f"cut:{fp.cut_shape}")

        metal = _normalize_metal(title)
        if metal:
            fp.metal_type = metal
            tokens.append(f"metal:{metal}")

        m = SETTING_PATTERN.search(title)
        if m:
            setting = re.sub(r'\s+', '-', m.group(1).strip().title())
            fp.setting_style = setting
            tokens.append(f"setting:{setting}")

        for pat in JEWELRY_DESIGN_PATTERNS:
            m = pat.search(title)
            if m:
                design = re.sub(r'\s+', '-', m.group(1).strip().title())
                fp.jewelry_design = design
                tokens.append(f"design:{design}")
                break

        m = JEWELRY_ITEM_PATTERN.search(title)
        if m:
            # Use as a secondary differentiator
            item = m.group(1).title()
            tokens.append(f"item:{item}")

        # P0: Diamond presence
        if HAS_DIAMONDS_PATTERN.search(title):
            fp.has_diamonds = "Diamond"
            tokens.append("diamond:Yes")
        else:
            fp.has_diamonds = "No-Diamond"
            tokens.append("diamond:No")

        # P0: Ring type (engagement vs wedding band vs set)
        m = RING_TYPE_PATTERN.search(title)
        if m:
            rtype = re.sub(r'\s+', '-', m.group(1).strip().title())
            fp.ring_type = rtype
            tokens.append(f"rtype:{rtype}")

        # P0: Necklace/chain length (for Tiffany etc.)
        if category == "Tiffany Elsa Peretti Necklace" or "necklace" in title.lower() or "chain" in title.lower():
            m = NECKLACE_LENGTH_PATTERN.search(title)
            if m:
                length = m.group(1)
                fp.necklace_length = f"{length}in"
                tokens.append(f"nlen:{length}in")

        # P1: Bracelet style (Cartier)
        if "cartier" in title.lower() or "love" in title.lower():
            m = BRACELET_STYLE_PATTERN.search(title)
            if m:
                style = re.sub(r'\s+', '-', m.group(1).strip().title())
                fp.bracelet_style = style
                tokens.append(f"bstyle:{style}")

            m = BRACELET_SIZE_PATTERN.search(title)
            if m:
                bsize = int(m.group(1))
                if 14 <= bsize <= 22:  # valid bracelet sizes
                    fp.bracelet_size = bsize
                    tokens.append(f"bsize:{bsize}")

    # ── Golf ─────────────────────────────────────────────────────────
    if category in GOLF_CATEGORIES or "driver" in title.lower() or "stealth" in title.lower():
        m = TAYLORMADE_MODEL_PATTERN.search(title)
        if m:
            variant = (m.group(1) or m.group(2) or "").strip().upper()
            model = "Stealth2" + (f"-{variant}" if variant else "")
            fp.golf_model = model
            tokens.append(f"golf:{model}")

        m = LOFT_PATTERN.search(title)
        if m:
            fp.loft_degrees = float(m.group(1))
            tokens.append(f"loft:{fp.loft_degrees}")

        m = FLEX_PATTERN.search(title)
        if m:
            fp.shaft_flex = _normalize_flex(m.group(1))
            tokens.append(f"flex:{fp.shaft_flex}")

        m = HANDEDNESS_PATTERN.search(title)
        if m:
            fp.handedness = _normalize_handedness(m.group(1))
            tokens.append(f"hand:{fp.handedness}")

        m = CLUB_COMPLETENESS_PATTERN.search(title)
        if m:
            fp.club_completeness = "HeadOnly" if "head" in m.group(1).lower() and "cover" not in m.group(1).lower() else "Headcover"
            tokens.append(f"club:{fp.club_completeness}")

    # ── Cycling ──────────────────────────────────────────────────────
    if category in CYCLING_CATEGORIES or any(
        w in title.lower() for w in ["ultegra", "dura-ace", "groupset", "shimano"]
    ):
        m = GROUPSET_MODEL_PATTERN.search(title)
        if m:
            model = (m.group(1) or m.group(2) or "").strip().upper()
            fp.groupset_model = model
            tokens.append(f"groupset:{model}")

        m = SPEED_COUNT_PATTERN.search(title)
        if m:
            if m.group(1):
                fp.speed_count = f"{m.group(1)}s"
            elif m.group(2) and m.group(3):
                fp.speed_count = f"{m.group(3)}s"
            elif m.group(4):
                fp.speed_count = f"{m.group(4)}s"
            if fp.speed_count:
                tokens.append(f"speed:{fp.speed_count}")

        if ELECTRONIC_PATTERN.search(title):
            fp.electronic_type = "Di2"
            tokens.append("type:Di2")
        elif category in CYCLING_CATEGORIES:
            fp.electronic_type = "Mechanical"
            tokens.append("type:Mechanical")

        m = BRAKE_TYPE_PATTERN.search(title)
        if m:
            brake = m.group(1).upper()
            if "RIM" in brake:
                fp.brake_type = "Rim"
            elif "DISC" in brake or brake == "DB":
                fp.brake_type = "Disc"
            elif "HYDRAULIC" in brake:
                fp.brake_type = "Disc"
            tokens.append(f"brake:{fp.brake_type}")

        # P1: Groupset discipline (TT/Triathlon vs Road)
        m = GROUPSET_DISCIPLINE_PATTERN.search(title)
        if m:
            disc = m.group(1).upper()
            if disc in ("TT", "TRIATHLON", "TIME TRIAL"):
                fp.groupset_discipline = "TT"
            elif disc in ("GRAVEL", "CX", "CYCLOCROSS"):
                fp.groupset_discipline = "Gravel"
            else:
                fp.groupset_discipline = "Road"
            tokens.append(f"disc:{fp.groupset_discipline}")

    # ── Luxury bags ──────────────────────────────────────────────────
    if category in BAG_CATEGORIES or any(
        w in title.lower() for w in ["chanel", "flap", "lambskin", "caviar", "neverfull", "birkin"]
    ):
        m = BAG_SIZE_PATTERN.search(title)
        if m:
            fp.bag_size = m.group(1).title()
            tokens.append(f"bagsize:{fp.bag_size}")

        m = LEATHER_PATTERN.search(title)
        if m:
            fp.leather_type = m.group(1).title()
            tokens.append(f"leather:{fp.leather_type}")

        m = HARDWARE_PATTERN.search(title)
        if m:
            hw = m.group(1).upper()
            fp.hardware_color = "GHW" if "GOLD" in hw or hw == "GHW" else "SHW"
            tokens.append(f"hw:{fp.hardware_color}")

        m = FLAP_PATTERN.search(title)
        if m:
            fp.flap_type = m.group(1).title()
            tokens.append(f"flap:{fp.flap_type}")

    # ── Artwork ──────────────────────────────────────────────────────
    if category in ART_CATEGORIES or "banksy" in title.lower():
        m = BANKSY_ARTWORK_PATTERN.search(title)
        if m:
            fp.artwork_name = m.group(1).title()
            tokens.append(f"artwork:{fp.artwork_name}")

        m = ART_FORMAT_PATTERN.search(title)
        if m:
            fp.art_format = m.group(1).title()
            tokens.append(f"format:{fp.art_format}")

        m = EDITION_PATTERN.search(title)
        if m:
            fp.edition_number = f"{m.group(1)}/{m.group(2)}"
            tokens.append(f"edition:{fp.edition_number}")

    # ── LEGO ─────────────────────────────────────────────────────────
    if category in LEGO_CATEGORIES or "lego" in title.lower():
        m = LEGO_SET_PATTERN.search(title)
        if m:
            set_num = m.group(1) or m.group(2) or m.group(3)
            if set_num:
                fp.lego_set_number = set_num
                tokens.append(f"lego:{set_num}")

    # ── Vinyl ────────────────────────────────────────────────────────
    # Only activate for vinyl categories or titles with strong vinyl indicators
    # (avoid "record" alone — too ambiguous across cameras, watches, etc.)
    if category in VINYL_CATEGORIES or (
        "vinyl" in title.lower() or "pressing" in title.lower()
    ):
        m = PRESSING_PATTERN.search(title)
        if m:
            pressing = re.sub(r'\s+', '-', m.group(1).strip().title())
            fp.pressing_type = pressing
            tokens.append(f"pressing:{pressing}")

        m = VINYL_GRADE_PATTERN.search(title)
        if m:
            grade = (m.group(1) or m.group(2) or "").upper()
            fp.vinyl_grade = grade
            tokens.append(f"grade:{grade}")

        m = CATALOG_PATTERN.search(title)
        if m:
            fp.catalog_number = m.group(1).upper().replace(" ", "")
            tokens.append(f"cat:{fp.catalog_number}")

        # P1: Vinyl format (LP vs 7" vs 12" etc.)
        m = VINYL_FORMAT_PATTERN.search(title)
        if m:
            fmt = m.group(1).strip().upper()
            if '7"' in fmt or "7IN" in fmt:
                fp.vinyl_format = "7in"
            elif '12"' in fmt or "12IN" in fmt:
                fp.vinyl_format = "12in"
            elif "DOUBLE" in fmt or "2X" in fmt or "2 X" in fmt:
                fp.vinyl_format = "2xLP"
            elif "TRIPLE" in fmt or "3X" in fmt or "3 X" in fmt:
                fp.vinyl_format = "3xLP"
            else:
                fp.vinyl_format = "LP"
            tokens.append(f"vfmt:{fp.vinyl_format}")

    # ── Guitars ──────────────────────────────────────────────────────
    if category in GUITAR_CATEGORIES or any(
        w in title.lower() for w in ["fender", "stratocaster", "telecaster", "gibson", "les paul"]
    ):
        # P0: Guitar brand
        m = GUITAR_BRAND_PATTERN.search(title)
        if m:
            brand = m.group(1).title()
            if brand.lower() in ("squire", "squier"):
                brand = "Squier"
            fp.guitar_brand = brand
            tokens.append(f"gbrand:{brand}")

        m = GUITAR_MODEL_PATTERN.search(title)
        if m:
            model = m.group(1).title().replace(" ", "-")
            # Normalize Strat -> Stratocaster
            if model.lower() in ("strat", "strato"):
                model = "Stratocaster"
            if model.lower() == "tele":
                model = "Telecaster"
            fp.guitar_model = model
            tokens.append(f"guitar:{model}")

        m = GUITAR_SERIES_PATTERN.search(title)
        if m:
            series = re.sub(r'\s+', '-', m.group(1).strip().title())
            fp.guitar_series = series
            tokens.append(f"series:{series}")

        m = GUITAR_ORIGIN_PATTERN.search(title)
        if m:
            origin = m.group(1).upper()
            if origin == "MIJ":
                origin = "JAPAN"
            elif origin == "MIM":
                origin = "MEXICO"
            elif origin == "MIA":
                origin = "USA"
            fp.origin_country = origin
            tokens.append(f"origin:{origin}")

    # ── Drums ────────────────────────────────────────────────────────
    if category in DRUM_CATEGORIES or "td-" in title.lower() or "v-drums" in title.lower():
        m = DRUM_MODULE_PATTERN.search(title)
        if m:
            module = m.group(1).upper().replace(" ", "")
            # Normalize TD17 -> TD-17
            module = re.sub(r'^TD(\d)', r'TD-\1', module)
            fp.drum_module = module
            tokens.append(f"drums:{module}")

    # ── Pushchairs ───────────────────────────────────────────────────
    if category in PUSHCHAIR_CATEGORIES or "bugaboo" in title.lower():
        m = BUGABOO_VERSION_PATTERN.search(title)
        if m:
            ver = (m.group(1) or "1").strip()
            if ver.lower() == "cub":
                fp.pushchair_version = "FoxCub"
            else:
                fp.pushchair_version = f"Fox{ver}"
            tokens.append(f"pushchair:{fp.pushchair_version}")

        if PUSHCHAIR_PARTS_PATTERN.search(title):
            fp.pushchair_type = "Parts"
            tokens.append("ptype:Parts")
        else:
            fp.pushchair_type = "Full"

    # ── Keyboards ────────────────────────────────────────────────────
    if category in KEYBOARD_CATEGORIES or "keychron" in title.lower():
        m = KEYCHRON_MODEL_PATTERN.search(title)
        if m:
            fp.keyboard_model = m.group(1).upper()
            tokens.append(f"kb:{fp.keyboard_model}")

        m = KEYBOARD_LAYOUT_PATTERN.search(title)
        if m:
            layout = m.group(1).upper().replace(" ", "")
            if "ISO" in layout and "UK" in layout:
                layout = "ISOUK"
            fp.keyboard_layout = layout
            tokens.append(f"layout:{layout}")

        m = SWITCH_PATTERN.search(title)
        if m:
            fp.switch_type = m.group(1).title()
            tokens.append(f"switch:{fp.switch_type}")

    # ── Bikes ────────────────────────────────────────────────────────
    if category in BIKE_CATEGORIES or "domane" in title.lower() or "tarmac" in title.lower():
        m = BIKE_TIER_PATTERN.search(title)
        if m:
            tier = m.group(1).upper().replace(" ", "")
            fp.bike_tier = tier
            tokens.append(f"tier:{tier}")

        m = FRAME_SIZE_PATTERN.search(title)
        if m:
            size = int(m.group(1))
            if 44 <= size <= 64:  # valid bike frame sizes
                fp.frame_size_cm = size
                tokens.append(f"frame:{size}cm")

    # ── Chainsaws ────────────────────────────────────────────────────
    if category in CHAINSAW_CATEGORIES or "stihl" in title.lower():
        m = STIHL_MODEL_PATTERN.search(title)
        if m:
            model = m.group(1).upper().replace(" ", "")
            fp.chainsaw_model = model
            tokens.append(f"saw:{model}")

        m = BAR_LENGTH_PATTERN.search(title)
        if m:
            length = int(m.group(1))
            if 8 <= length <= 36:  # valid bar lengths
                fp.bar_length_inches = length
                tokens.append(f"bar:{length}in")

        m = POWER_SOURCE_PATTERN.search(title)
        if m:
            src = m.group(1).title()
            if src == "Cordless":
                src = "Battery"
            fp.power_source = src
            tokens.append(f"power:{src}")

    # ── Signed shirts ────────────────────────────────────────────────
    if category in SHIRT_CATEGORIES or "signed" in title.lower():
        m = TEAM_PATTERN.search(title)
        if m:
            fp.team_name = m.group(1).title()
            tokens.append(f"team:{fp.team_name}")

        m = SEASON_PATTERN.search(title)
        if m:
            fp.season = m.group(1)
            tokens.append(f"season:{fp.season}")

        # P1: Signature type (printed vs hand-signed)
        m = SIGNATURE_TYPE_PATTERN.search(title)
        if m:
            sig = m.group(1).upper()
            if "PRINT" in sig:
                fp.signature_type = "Printed"
            else:
                fp.signature_type = "Hand-Signed"
            tokens.append(f"sig:{fp.signature_type}")

        # P1: Framed
        if FRAMED_PATTERN.search(title):
            fp.framed = "Framed"
            tokens.append("frame:Framed")

        # Shirt item type (shirt vs shorts vs frame)
        if re.search(r'\b(Shorts?|Boot|Medal|Cap)\b', title, re.IGNORECASE):
            fp.shirt_item_type = "Other"
            tokens.append("stype:Other")
        else:
            fp.shirt_item_type = "Shirt"

    # ── Jeans ────────────────────────────────────────────────────────
    if category in JEANS_CATEGORIES or "501" in title or "levis" in title.lower():
        m = JEANS_SIZE_PATTERN.search(title)
        if m:
            if m.group(1) and m.group(2):
                w, l = m.group(1), m.group(2)
            else:
                w, l = m.group(3), m.group(4)
            fp.waist_length = f"W{w}L{l}"
            tokens.append(f"jeans:{fp.waist_length}")

        m = DENIM_COLOR_PATTERN.search(title)
        if m:
            color = m.group(1).title().replace(" ", "-")
            fp.denim_color = color
            tokens.append(f"wash:{color}")

        m = MADE_IN_PATTERN.search(title)
        if m:
            fp.made_in = m.group(1).upper()
            tokens.append(f"made:{fp.made_in}")

    # ── Console editions ─────────────────────────────────────────────
    if category in CONSOLE_CATEGORIES or "switch" in title.lower():
        m = CONSOLE_EDITION_PATTERN.search(title)
        if m:
            edition = m.group(1).title().replace(" ", "-")
            fp.console_edition = edition
            tokens.append(f"edition:{edition}")

    # ── Backpacks ────────────────────────────────────────────────────
    if category in BACKPACK_CATEGORIES or "atmos" in title.lower():
        m = PACK_VOLUME_PATTERN.search(title)
        if m:
            vol = int(m.group(1) or m.group(2))
            if 20 <= vol <= 85:  # valid backpack volumes
                fp.pack_volume_l = vol
                tokens.append(f"vol:{vol}L")

    # ── reMarkable ───────────────────────────────────────────────────
    if category in REMARKABLE_CATEGORIES or "remarkable" in title.lower():
        m = REMARKABLE_MODEL_PATTERN.search(title)
        if m:
            model = re.sub(r'\s+', '-', m.group(1).strip().title())
            fp.remarkable_model = model
            tokens.append(f"rmk:{model}")

    # ── Whoop ────────────────────────────────────────────────────────
    if category in WHOOP_CATEGORIES or "whoop" in title.lower():
        m = WHOOP_VERSION_PATTERN.search(title)
        if m:
            fp.whoop_version = m.group(1)
            tokens.append(f"whoop:{m.group(1)}")

        # P0: Product type (strap vs full tracker)
        m = WHOOP_PRODUCT_PATTERN.search(title)
        if m:
            prod = m.group(1).upper()
            if "TRACKER" in prod or "SENSOR" in prod:
                fp.product_completeness = "Tracker"
                fp.whoop_item = "Tracker"
            elif "CHARGER" in prod:
                fp.product_completeness = "Charger"
                fp.whoop_item = "Charger"
            else:
                fp.product_completeness = "Strap-Only"
                fp.whoop_item = "Strap"
            tokens.append(f"wprod:{fp.product_completeness}")
        elif any(w in title.lower() for w in ["battery", "sensor", "device"]):
            fp.product_completeness = "Tracker"
            fp.whoop_item = "Tracker"
            tokens.append("wprod:Tracker")

    # ── iRobot Roomba ──────────────────────────────────────────────
    if category in ROOMBA_CATEGORIES or "roomba" in title.lower():
        # P0: Roomba model (S9, S9+, j7, j7+, Max 705, 205, etc.)
        m = ROOMBA_MODEL_PATTERN.search(title)
        if m:
            model = m.group(1).strip().upper().replace(" ", "")
            fp.roomba_model = model
            tokens.append(f"roomba:{model}")

        # P0: Completeness (Robot Only vs with Base vs Combo)
        m = IROBOT_COMPLETENESS_PATTERN.search(title)
        if m:
            comp = m.group(1).upper()
            if "ONLY" in comp and ("BASE" in comp or "DOCK" in comp):
                fp.product_completeness = "Base-Only"
            elif "ONLY" in comp or "NO " in comp:
                fp.product_completeness = "Robot-Only"
            elif "CLEAN BASE" in comp or "AUTOEMPTY" in comp or "SELF" in comp:
                fp.product_completeness = "With-Base"
            tokens.append(f"rcomp:{fp.product_completeness}")
        elif "combo" in title.lower():
            fp.product_completeness = "Combo"
            tokens.append("rcomp:Combo")

    # ── Antique watches ──────────────────────────────────────────────
    if category in WATCH_CATEGORIES or "pocket watch" in title.lower() or "fob" in title.lower():
        # Priority: non-watch items first (fob medals, chains, accessories)
        # because titles like "Pocket Watch Fob Medal" should classify as Fob Medal
        _non_watch = re.search(
            r'\b(Fob\s*Medal|Albert(?:ina)?\s*(?:Chain)?|Watch\s*Chain)\b',
            title, re.IGNORECASE
        )
        if _non_watch:
            item = re.sub(r'\s+', '-', _non_watch.group(1).strip().title())
        else:
            m = WATCH_ITEM_PATTERN.search(title)
            item = re.sub(r'\s+', '-', m.group(1).strip().title()) if m else None
        if item:
            fp.watch_item_type = item
            tokens.append(f"wtype:{item}")

        m = WATCH_MECHANISM_PATTERN.search(title)
        if m:
            fp.watch_mechanism = m.group(1).title()
            tokens.append(f"mech:{fp.watch_mechanism}")

        m = WATCH_WORKING_PATTERN.search(title)
        if m:
            status = m.group(1).strip().title()
            if status in ("W/O", "Needs Repair"):
                status = "Not-Working"
            elif status in ("Ticks", "Fully Running"):
                status = "Running"
            fp.watch_working = re.sub(r'\s+', '-', status)
            tokens.append(f"works:{fp.watch_working}")

    fp.raw_tokens = tuple(sorted(tokens))
    return fp


# ═══════════════════════════════════════════════════════════════════════════
# DESCRIPTION LABELS
# ═══════════════════════════════════════════════════════════════════════════

def describe_diff(diffs: dict) -> str:
    """Generate human-readable reasoning for a spec mismatch."""
    labels = {
        "cpu": "CPU", "ram_gb": "RAM", "storage_gb": "Storage",
        "screen_inches": "Screen size", "wattage": "Wattage",
        "voltage": "Voltage", "capacity_mah": "Battery",
        "size_label": "Size", "generation": "Generation",
        "quantity": "Quantity",
        # New v8
        "carat": "Carat weight", "cut_shape": "Cut shape",
        "metal_type": "Metal", "stone_type": "Stone",
        "setting_style": "Setting", "jewelry_design": "Design",
        "golf_model": "Golf model", "loft_degrees": "Loft",
        "shaft_flex": "Shaft flex", "handedness": "Handedness",
        "club_completeness": "Club type",
        "groupset_model": "Groupset model", "speed_count": "Speed",
        "electronic_type": "Electronic type", "brake_type": "Brake type",
        "bag_size": "Bag size", "leather_type": "Leather",
        "hardware_color": "Hardware", "flap_type": "Flap type",
        "artwork_name": "Artwork", "art_format": "Format",
        "edition_number": "Edition",
        "lego_set_number": "LEGO set #",
        "pressing_type": "Pressing", "vinyl_grade": "Grade",
        "catalog_number": "Catalog #",
        "guitar_model": "Guitar model", "guitar_series": "Series",
        "origin_country": "Origin",
        "drum_module": "Drum module",
        "pushchair_version": "Pushchair version",
        "pushchair_type": "Item type",
        "keyboard_model": "Keyboard model",
        "keyboard_layout": "Layout", "switch_type": "Switch",
        "bike_tier": "Bike tier", "frame_size_cm": "Frame size",
        "chainsaw_model": "Chainsaw model",
        "bar_length_inches": "Bar length", "power_source": "Power",
        "team_name": "Team", "season": "Season",
        "waist_length": "Jeans size", "denim_color": "Color",
        "made_in": "Made in",
        "console_edition": "Edition",
        "pack_volume_l": "Volume",
        "remarkable_model": "Model",
        "whoop_version": "Version",
        "watch_item_type": "Watch item type",
        "watch_mechanism": "Mechanism",
        "watch_working": "Working status",
        # P0/P1 additions
        "product_brand": "Product brand",
        "guitar_brand": "Guitar brand",
        "has_diamonds": "Diamonds",
        "ring_type": "Ring type",
        "necklace_length": "Necklace length",
        "roomba_model": "Roomba model",
        "product_completeness": "Completeness",
        "bracelet_style": "Bracelet style",
        "bracelet_size": "Bracelet size",
        "signature_type": "Signature type",
        "framed": "Framed",
        "vinyl_format": "Vinyl format",
        "groupset_discipline": "Discipline",
        "whoop_item": "Whoop item type",
        "shirt_item_type": "Shirt item type",
    }
    parts = []
    for key, (val_a, val_b) in diffs.items():
        label = labels.get(key, key)
        parts.append(f"Different {label}: {val_a} vs {val_b}")
    return "; ".join(parts)


# ═══════════════════════════════════════════════════════════════════════════
# DATABASE
# ═══════════════════════════════════════════════════════════════════════════

@dataclass
class PairRow:
    rel_id: int
    listing_id_a: int
    listing_id_b: int
    is_comparable: bool
    similarity_score: float
    ebay_id_a: str
    title_a: str
    desc_a: Optional[str]
    job_id_a: int
    search_term_a: str
    ebay_id_b: str
    title_b: str
    desc_b: Optional[str]
    job_id_b: int
    search_term_b: str


def load_comparable_pairs(conn) -> list[PairRow]:
    """Load all IsComparable=1 pairs with listing details."""
    print("Loading comparable pairs from database...")

    query = """
        SELECT
            lr.Id, lr.ListingIdA, lr.ListingIdB,
            lr.IsComparable, lr.SimilarityScore,
            la.ListingId, la.Title, la.Description, la.ScrapeJobId,
            ISNULL(ja.SearchTerm, 'Unknown'),
            lb.ListingId, lb.Title, lb.Description, lb.ScrapeJobId,
            ISNULL(jb.SearchTerm, 'Unknown')
        FROM ListingRelationships lr
        INNER JOIN Listings la ON la.Id = lr.ListingIdA
        INNER JOIN Listings lb ON lb.Id = lr.ListingIdB
        LEFT JOIN ScrapeJobs ja ON ja.Id = la.ScrapeJobId
        LEFT JOIN ScrapeJobs jb ON jb.Id = lb.ScrapeJobId
        WHERE lr.IsComparable = 1
          AND la.Title IS NOT NULL
          AND lb.Title IS NOT NULL
    """

    cursor = conn.cursor()
    cursor.execute(query)

    pairs = []
    row_count = 0
    for row in cursor:
        row_count += 1
        pairs.append(PairRow(
            rel_id=row[0],
            listing_id_a=row[1],
            listing_id_b=row[2],
            is_comparable=bool(row[3]),
            similarity_score=float(row[4]) if row[4] else 0.0,
            ebay_id_a=str(row[5]),
            title_a=str(row[6]) if row[6] else "",
            desc_a=str(row[7]) if row[7] else None,
            job_id_a=int(row[8]) if row[8] else 0,
            search_term_a=str(row[9]),
            ebay_id_b=str(row[10]),
            title_b=str(row[11]) if row[11] else "",
            desc_b=str(row[12]) if row[12] else None,
            job_id_b=int(row[13]) if row[13] else 0,
            search_term_b=str(row[14]),
        ))

        if row_count % 50000 == 0:
            print(f"  ...loaded {row_count:,} pairs")

    print(f"  Loaded {len(pairs):,} comparable pairs total")
    return pairs


# ═══════════════════════════════════════════════════════════════════════════
# MINING
# ═══════════════════════════════════════════════════════════════════════════

@dataclass
class MinedPair:
    anchor_id: str
    neighbor_id: str
    job_id: int
    product_name: str
    anchor_title: str
    neighbor_title: str
    anchor_desc: Optional[str]
    neighbor_desc: Optional[str]
    label: int
    confidence: str
    reasoning: str
    source: str


def mine_pairs(
    pairs: list[PairRow],
    max_neg_per_cat: int,
    max_pos_per_cat: int,
) -> tuple[list[MinedPair], dict]:
    print("\nExtracting spec fingerprints and analyzing pairs...")

    stats = {
        "total_pairs": len(pairs),
        "both_have_specs": 0,
        "one_has_specs": 0,
        "neither_has_specs": 0,
        "false_positives": 0,
        "confirmed_positives": 0,
        "by_category": defaultdict(lambda: {
            "total": 0, "false_pos": 0, "confirmed_pos": 0,
            "no_specs": 0, "diff_details": defaultdict(int)
        }),
    }

    false_pos_by_cat = defaultdict(list)
    confirmed_pos_by_cat = defaultdict(list)

    for i, pair in enumerate(pairs):
        if (i + 1) % 50000 == 0:
            print(f"  ...analyzed {i + 1:,}/{len(pairs):,} pairs")

        # Pass category for context-aware extraction
        fp_a = extract_fingerprint(pair.title_a, pair.search_term_a)
        fp_b = extract_fingerprint(pair.title_b, pair.search_term_b)

        category = pair.search_term_a
        cat_stats = stats["by_category"][category]
        cat_stats["total"] += 1

        if fp_a.has_specs and fp_b.has_specs:
            stats["both_have_specs"] += 1
            diffs = fp_a.diff(fp_b)

            if diffs:
                stats["false_positives"] += 1
                cat_stats["false_pos"] += 1
                for diff_key in diffs:
                    cat_stats["diff_details"][diff_key] += 1
                false_pos_by_cat[category].append((pair, diffs))
            else:
                stats["confirmed_positives"] += 1
                cat_stats["confirmed_pos"] += 1
                confirmed_pos_by_cat[category].append(pair)

        elif fp_a.has_specs or fp_b.has_specs:
            stats["one_has_specs"] += 1
            cat_stats["no_specs"] += 1
        else:
            stats["neither_has_specs"] += 1
            cat_stats["no_specs"] += 1

    mined = []

    for category, candidates in false_pos_by_cat.items():
        candidates.sort(key=lambda x: len(x[1]))
        for pair, diffs in candidates[:max_neg_per_cat]:
            reasoning = describe_diff(diffs)
            mined.append(MinedPair(
                anchor_id=pair.ebay_id_a,
                neighbor_id=pair.ebay_id_b,
                job_id=pair.job_id_a,
                product_name=pair.search_term_a,
                anchor_title=pair.title_a,
                neighbor_title=pair.title_b,
                anchor_desc=pair.desc_a or "",
                neighbor_desc=pair.desc_b or "",
                label=0,
                confidence="high",
                reasoning=reasoning,
                source="false_positive_mined",
            ))

    for category, candidates in confirmed_pos_by_cat.items():
        for pair in candidates[:max_pos_per_cat]:
            fp_a = extract_fingerprint(pair.title_a, pair.search_term_a)
            specs_str = ", ".join(fp_a.raw_tokens) if fp_a.raw_tokens else "matching specs"
            mined.append(MinedPair(
                anchor_id=pair.ebay_id_a,
                neighbor_id=pair.ebay_id_b,
                job_id=pair.job_id_a,
                product_name=pair.search_term_a,
                anchor_title=pair.title_a,
                neighbor_title=pair.title_b,
                anchor_desc=pair.desc_a or "",
                neighbor_desc=pair.desc_b or "",
                label=1,
                confidence="high",
                reasoning=f"Same specifications confirmed: {specs_str}",
                source="confirmed_positive_mined",
            ))

    return mined, stats


def print_stats(stats: dict):
    total = stats["total_pairs"]
    print(f"\n{'='*70}")
    print(f"ANALYSIS SUMMARY")
    print(f"{'='*70}")
    print(f"Total comparable pairs analyzed:  {total:,}")
    print(f"Both listings have specs:         {stats['both_have_specs']:,} ({100*stats['both_have_specs']/total:.1f}%)")
    print(f"Only one has specs:               {stats['one_has_specs']:,} ({100*stats['one_has_specs']/total:.1f}%)")
    print(f"Neither has specs:                {stats['neither_has_specs']:,} ({100*stats['neither_has_specs']/total:.1f}%)")
    print()
    print(f"FALSE POSITIVES (specs differ):   {stats['false_positives']:,} ({100*stats['false_positives']/total:.1f}%)")
    print(f"CONFIRMED POSITIVES (specs match):{stats['confirmed_positives']:,} ({100*stats['confirmed_positives']/total:.1f}%)")

    print(f"\n{'='*70}")
    print(f"PER-CATEGORY BREAKDOWN (sorted by false positive count)")
    print(f"{'='*70}")
    print(f"{'Category':<40} {'Total':>7} {'FP':>7} {'FP%':>6} {'TP':>7} {'NoSpec':>7}")
    print(f"{'-'*40} {'-'*7} {'-'*7} {'-'*6} {'-'*7} {'-'*7}")

    sorted_cats = sorted(
        stats["by_category"].items(),
        key=lambda x: x[1]["false_pos"],
        reverse=True
    )

    for cat, cs in sorted_cats:
        if cs["total"] == 0:
            continue
        fp_pct = 100 * cs["false_pos"] / cs["total"] if cs["total"] > 0 else 0
        print(
            f"{cat[:40]:<40} {cs['total']:>7,} {cs['false_pos']:>7,} "
            f"{fp_pct:>5.1f}% {cs['confirmed_pos']:>7,} {cs['no_specs']:>7,}"
        )
        if cs["diff_details"]:
            details = ", ".join(
                f"{k}({v})" for k, v in
                sorted(cs["diff_details"].items(), key=lambda x: -x[1])
            )
            print(f"  {'':>40} diffs: {details}")


def write_csv(mined: list[MinedPair], output_path: Path):
    print(f"\nWriting {len(mined):,} pairs to {output_path}...")

    with open(output_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow([
            "anchor_id", "neighbor_id", "job_id", "product_name",
            "anchor_title", "neighbor_title", "anchor_desc", "neighbor_desc",
            "label", "confidence", "reasoning", "source"
        ])
        for p in mined:
            writer.writerow([
                p.anchor_id, p.neighbor_id, p.job_id, p.product_name,
                p.anchor_title, p.neighbor_title, p.anchor_desc, p.neighbor_desc,
                p.label, p.confidence, p.reasoning, p.source
            ])

    print(f"  Done. {len(mined):,} pairs written.")


# ═══════════════════════════════════════════════════════════════════════════
# MAIN
# ═══════════════════════════════════════════════════════════════════════════

def main():
    parser = argparse.ArgumentParser(description="Mine hard pairs from ListingRelationships (v8)")
    parser.add_argument("--dry-run", action="store_true", help="Analyze only, don't write CSV")
    parser.add_argument("--max-neg-per-cat", type=int, default=DEFAULT_MAX_HARD_NEG_PER_CAT)
    parser.add_argument("--max-pos-per-cat", type=int, default=DEFAULT_MAX_HARD_POS_PER_CAT)
    parser.add_argument("--test-extract", type=str, nargs="+",
                        help="Test extraction on one or more titles (no DB needed)")
    parser.add_argument("--test-category", type=str, default="",
                        help="Category to use with --test-extract")
    args = parser.parse_args()

    # Quick test mode: extract specs from titles without touching DB
    if args.test_extract:
        for title in args.test_extract:
            fp = extract_fingerprint(title, args.test_category)
            print(f"\nTitle: {title}")
            print(f"Category: {args.test_category or '(none)'}")
            print(f"Tokens: {fp.raw_tokens}")
            specs = fp.spec_dict()
            if specs:
                for k, v in sorted(specs.items()):
                    print(f"  {k}: {v}")
            else:
                print("  (no specs extracted)")
        return

    print("Connecting to database...")
    conn = pyodbc.connect(DB_CONN)

    pairs = load_comparable_pairs(conn)
    conn.close()

    mined, stats = mine_pairs(pairs, args.max_neg_per_cat, args.max_pos_per_cat)
    print_stats(stats)

    hard_neg = sum(1 for p in mined if p.label == 0)
    hard_pos = sum(1 for p in mined if p.label == 1)
    print(f"\n{'='*70}")
    print(f"MINED PAIRS")
    print(f"{'='*70}")
    print(f"Hard negatives (label=0):  {hard_neg:,}")
    print(f"Hard positives (label=1):  {hard_pos:,}")
    print(f"Total:                     {len(mined):,}")

    if not args.dry_run and mined:
        write_csv(mined, OUTPUT_CSV)
    elif args.dry_run:
        print("\n[DRY RUN] No CSV written.")
    else:
        print("\nNo pairs to write.")


if __name__ == "__main__":
    main()
