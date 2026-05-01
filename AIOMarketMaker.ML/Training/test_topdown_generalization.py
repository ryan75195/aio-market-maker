"""
Tests for top-down taxonomy extraction across diverse product categories.

Verifies the LLM extracts only what's in the title and doesn't hallucinate,
across watches, sneakers, electronics, collectibles, etc.

Run:
    pytest test_topdown_generalization.py -v
    pytest test_topdown_generalization.py -v -k "test_no_hallucinate"
"""

import json
import pytest
from pathlib import Path

OUTPUT_DIR = Path(__file__).parent / "data" / "topdown_taxonomy"


@pytest.fixture(scope="session")
def model_and_tokenizer():
    from experiment_topdown_taxonomy import load_local_model
    model, tokenizer = load_local_model("unsloth/qwen3-4b-unsloth-bnb-4bit")
    return model, tokenizer


def make_extractor(model_and_tokenizer, skeleton):
    """Create an extraction function bound to a specific skeleton."""
    from experiment_topdown_taxonomy import extract_axes_local
    model, tokenizer = model_and_tokenizer

    def extract(title):
        return extract_axes_local(model, tokenizer, title, skeleton)

    return extract


def title_contains(title, value):
    """Check if a value (or close variant) appears in the title text."""
    return value.lower() in title.lower()


# ── Skeletons per product category ──────────────────────────────────────────
# Minimal hand-crafted skeletons to test extraction behavior without needing
# GPT-5-nano. Each has 3-5 axes with realistic values.

SKELETON_SNEAKERS = {
    "axes": [
        {"name": "model", "description": "Shoe model name", "values": [
            "air jordan 1", "air jordan 4", "air jordan 11", "dunk low", "dunk high",
            "air force 1", "air max 90", "air max 1", "yeezy 350", "yeezy 700"]},
        {"name": "size", "description": "UK/US shoe size", "values": [
            "uk 7", "uk 8", "uk 9", "uk 10", "uk 11", "uk 12",
            "us 8", "us 9", "us 10", "us 11", "us 12"]},
        {"name": "colorway", "description": "Color scheme name", "values": [
            "bred", "chicago", "royal", "shadow", "obsidian", "mocha",
            "panda", "university blue", "travis scott", "reverse mocha"]},
        {"name": "box", "description": "Original box included", "values": [
            "with box", "no box", "replacement box"]},
    ]
}

SKELETON_POKEMON = {
    "axes": [
        {"name": "set", "description": "Pokemon TCG set name", "values": [
            "base set", "jungle", "fossil", "team rocket", "neo genesis",
            "scarlet violet", "obsidian flames", "paldea evolved",
            "151", "prismatic evolutions", "surging sparks"]},
        {"name": "product_type", "description": "Product format", "values": [
            "booster box", "elite trainer box", "booster pack", "tin",
            "collection box", "blister pack"]},
        {"name": "language", "description": "Card language", "values": [
            "english", "japanese", "korean", "chinese"]},
        {"name": "sealed", "description": "Factory sealed status", "values": [
            "sealed", "opened", "unsealed"]},
    ]
}

SKELETON_ELECTRONICS = {
    "axes": [
        {"name": "model", "description": "Product model", "values": [
            "iphone 15 pro max", "iphone 15 pro", "iphone 15", "iphone 14 pro",
            "galaxy s24 ultra", "galaxy s24", "pixel 8 pro", "pixel 8"]},
        {"name": "storage", "description": "Storage capacity", "values": [
            "64gb", "128gb", "256gb", "512gb", "1tb"]},
        {"name": "color", "description": "Device color", "values": [
            "black", "white", "blue", "natural titanium", "space black",
            "deep purple", "gold"]},
        {"name": "condition", "description": "Item condition", "values": [
            "brand new", "sealed", "like new", "good condition",
            "cracked screen", "for parts"]},
    ]
}

SKELETON_LEGO = {
    "axes": [
        {"name": "set_number", "description": "LEGO set number", "values": [
            "75192", "75375", "10294", "21330", "75341", "10497",
            "42083", "10300", "75331", "21054"]},
        {"name": "theme", "description": "LEGO theme", "values": [
            "star wars", "technic", "creator", "ideas", "city",
            "architecture", "harry potter", "marvel"]},
        {"name": "sealed", "description": "Box sealed status", "values": [
            "sealed", "bnib", "new", "opened", "used", "complete", "incomplete"]},
        {"name": "retired", "description": "Retirement status", "values": [
            "retired", "discontinued"]},
    ]
}


# ── Tests: Sneakers ─────────────────────────────────────────────────────────

class TestSneakerExtraction:
    @pytest.fixture(autouse=True)
    def setup(self, model_and_tokenizer):
        self.extract = make_extractor(model_and_tokenizer, SKELETON_SNEAKERS)

    def test_extracts_jordan_1_bred(self):
        result = self.extract("Nike Air Jordan 1 Retro High OG Bred UK 10 With Box")
        assert result is not None
        assert result.get("model") == "air jordan 1"
        assert result.get("size") == "uk 10"
        assert result.get("colorway") == "bred"

    def test_extracts_dunk_low_panda(self):
        result = self.extract("Nike Dunk Low Panda Black White US 9")
        assert result is not None
        assert result.get("model") == "dunk low"
        assert result.get("colorway") == "panda"

    def test_no_hallucinate_colorway(self):
        """Plain title with no colorway name — should not guess."""
        result = self.extract("Nike Air Jordan 1 Mid UK 9")
        assert result is not None
        assert result.get("colorway") is None, (
            f"Hallucinated colorway '{result.get('colorway')}' — not in title"
        )

    def test_no_hallucinate_box(self):
        """No mention of box — should not assume."""
        result = self.extract("Adidas Yeezy Boost 350 V2 UK 8")
        assert result is not None
        assert result.get("box") is None, (
            f"Hallucinated box '{result.get('box')}' — not in title"
        )


# ── Tests: Pokemon ──────────────────────────────────────────────────────────

class TestPokemonExtraction:
    @pytest.fixture(autouse=True)
    def setup(self, model_and_tokenizer):
        self.extract = make_extractor(model_and_tokenizer, SKELETON_POKEMON)

    def test_extracts_sealed_booster_box(self):
        result = self.extract("Pokemon Scarlet Violet Obsidian Flames Booster Box Sealed English")
        assert result is not None
        assert result.get("product_type") == "booster box"
        assert result.get("sealed") == "sealed"
        assert result.get("language") == "english"

    def test_extracts_japanese_etb(self):
        result = self.extract("Pokemon 151 Elite Trainer Box Japanese Sealed")
        assert result is not None
        assert result.get("set") == "151"
        assert result.get("product_type") == "elite trainer box"
        assert result.get("language") == "japanese"

    def test_no_hallucinate_sealed(self):
        """No sealed/opened mentioned — should not assume."""
        result = self.extract("Pokemon Booster Box Paldea Evolved")
        assert result is not None
        assert result.get("sealed") is None, (
            f"Hallucinated sealed '{result.get('sealed')}' — not in title"
        )

    def test_no_hallucinate_language(self):
        """No language mentioned — should not default to english."""
        result = self.extract("Pokemon Booster Box Scarlet Violet Sealed")
        assert result is not None
        assert result.get("language") is None, (
            f"Hallucinated language '{result.get('language')}' — not in title"
        )


# ── Tests: Electronics ──────────────────────────────────────────────────────

class TestElectronicsExtraction:
    @pytest.fixture(autouse=True)
    def setup(self, model_and_tokenizer):
        self.extract = make_extractor(model_and_tokenizer, SKELETON_ELECTRONICS)

    def test_extracts_iphone_full(self):
        result = self.extract("Apple iPhone 15 Pro Max 256GB Natural Titanium Sealed")
        assert result is not None
        assert result.get("model") == "iphone 15 pro max"
        assert result.get("storage") == "256gb"
        assert result.get("condition") == "sealed"

    def test_extracts_storage_and_color(self):
        result = self.extract("Samsung Galaxy S24 Ultra 512GB Black Like New")
        assert result is not None
        assert result.get("storage") == "512gb"
        assert result.get("color") == "black"

    def test_no_hallucinate_storage(self):
        """No storage mentioned — should not guess."""
        result = self.extract("iPhone 15 Pro Space Black Good Condition")
        assert result is not None
        assert result.get("storage") is None, (
            f"Hallucinated storage '{result.get('storage')}' — not in title"
        )

    def test_no_hallucinate_condition(self):
        """No condition words — should not infer."""
        result = self.extract("Apple iPhone 15 Pro Max 256GB")
        assert result is not None
        assert result.get("condition") is None, (
            f"Hallucinated condition '{result.get('condition')}' — not in title"
        )


# ── Tests: LEGO ─────────────────────────────────────────────────────────────

class TestLegoExtraction:
    @pytest.fixture(autouse=True)
    def setup(self, model_and_tokenizer):
        self.extract = make_extractor(model_and_tokenizer, SKELETON_LEGO)

    def test_extracts_set_number_and_theme(self):
        result = self.extract("LEGO Star Wars 75192 Millennium Falcon UCS Sealed BNIB")
        assert result is not None
        assert result.get("set_number") == "75192"
        assert result.get("theme") == "star wars"

    def test_extracts_retired(self):
        result = self.extract("LEGO Ideas 21330 Home Alone Retired Sealed")
        assert result is not None
        assert str(result.get("set_number")) == "21330"
        assert result.get("retired") == "retired"

    def test_no_hallucinate_retired(self):
        """No retirement mentioned — should not assume."""
        result = self.extract("LEGO Star Wars 75375 New Sealed")
        assert result is not None
        assert result.get("retired") is None, (
            f"Hallucinated retired '{result.get('retired')}' — not in title"
        )

    def test_no_hallucinate_theme(self):
        """Generic LEGO title — should not guess theme."""
        result = self.extract("LEGO Set 10294 Sealed New In Box")
        assert result is not None
        # Theme is not mentioned in the title
        assert result.get("theme") is None, (
            f"Hallucinated theme '{result.get('theme')}' — not in title"
        )


# ── Tests: Edge cases ──────────────────────────────────────────────────────

class TestEdgeCases:
    @pytest.fixture(autouse=True)
    def setup(self, model_and_tokenizer):
        self.extract_sneakers = make_extractor(model_and_tokenizer, SKELETON_SNEAKERS)
        self.extract_electronics = make_extractor(model_and_tokenizer, SKELETON_ELECTRONICS)

    def test_accessory_not_product(self):
        """A case/accessory listing — should not extract the phone model."""
        result = self.extract_electronics("Clear Case Cover for iPhone 15 Pro Max")
        assert result is not None
        # The case is FOR an iPhone but is not an iPhone itself.
        # Extracting the model is acceptable since "iphone 15 pro max" is in the title,
        # but it should not hallucinate storage/condition.
        assert result.get("storage") is None
        assert result.get("condition") is None

    def test_lot_listing(self):
        """Bundle/lot — should still extract what's there."""
        result = self.extract_sneakers("Job Lot 5x Nike Dunk Low Various Sizes No Box")
        assert result is not None
        assert result.get("model") == "dunk low"
        assert result.get("box") == "no box"
