"""
Tests for top-down taxonomy LLM extraction.

Core principle: ONLY extract what's in the title. Never infer or hallucinate.

Run:
    pytest test_topdown_extraction.py -v
    pytest test_topdown_extraction.py -v -k "test_no_hallucinate"
"""

import json
import pytest
from pathlib import Path

# ── Load skeleton and model once for all tests ──────────────────────────────

SKELETON_PATH = Path(__file__).parent / "data" / "topdown_taxonomy" / "skeleton_1030.json"


@pytest.fixture(scope="session")
def skeleton():
    with open(SKELETON_PATH) as f:
        return json.load(f)


@pytest.fixture(scope="session")
def extractor(skeleton):
    """Load model once, return a function that extracts axes from a title."""
    from experiment_topdown_taxonomy import load_local_model, extract_axes_local

    model, tokenizer = load_local_model("unsloth/qwen3-4b-unsloth-bnb-4bit")

    def extract(title):
        return extract_axes_local(model, tokenizer, title, skeleton)

    return extract


# ── Tests: correct extraction ───────────────────────────────────────────────


class TestCorrectExtraction:
    """LLM should extract values that ARE in the title."""

    def test_extracts_reference_number(self, extractor):
        result = extractor("Rolex Submariner 16610 Black Steel 40mm")
        assert result is not None
        assert result.get("reference") == "16610"

    def test_extracts_size(self, extractor):
        result = extractor("Rolex Submariner 41mm Date Steel 126610LN")
        assert result is not None
        assert result.get("size") == "41mm"

    def test_extracts_material_two_tone(self, extractor):
        result = extractor("Rolex 16613LN Submariner Two Tone Black 40mm")
        assert result is not None
        assert "two" in result.get("material", "").lower()

    def test_extracts_box_and_papers(self, extractor):
        result = extractor("Rolex Submariner 16610 Box & Papers 2009")
        assert result is not None
        bp = result.get("box_papers", "").lower()
        assert "box" in bp and "paper" in bp

    def test_extracts_year(self, extractor):
        result = extractor("2023 Rolex Submariner Date 126610LN Steel")
        assert result is not None
        assert result.get("year") == "2023"

    def test_extracts_green_bezel(self, extractor):
        result = extractor("Rolex Submariner HULK Green Bezel Green Dial 116610LV")
        assert result is not None
        assert result.get("bezel_color") == "green"
        assert result.get("dial_color") == "green"

    def test_extracts_papers_mentioned(self, extractor):
        result = extractor("ROLEX MENS SUBMARINER 16610 BLACK STEEL 40MM w/ PAPER")
        assert result is not None
        bp = result.get("box_papers", "").lower()
        assert "paper" in bp, f"Should extract paper-related value, got '{bp}'"


# ── Tests: no hallucination ─────────────────────────────────────────────────


class TestNoHallucination:
    """LLM must NOT invent values absent from the title."""

    def test_no_reference_when_absent(self, extractor):
        """Title has no reference number — should not hallucinate one."""
        result = extractor("Rolex Submariner Date Black Steel Watch")
        assert result is not None
        assert result.get("reference") is None, (
            f"Hallucinated reference '{result.get('reference')}' — not in title"
        )

    def test_no_year_when_absent(self, extractor):
        """No year in title — should not guess one."""
        result = extractor("Rolex Submariner 116610LN Black Ceramic 40mm")
        assert result is not None
        assert result.get("year") is None, (
            f"Hallucinated year '{result.get('year')}' — not in title"
        )

    def test_no_condition_when_absent(self, extractor):
        """No condition words in title — should not infer one."""
        result = extractor("Rolex Submariner 16610 Black Steel 40mm 2009")
        assert result is not None
        assert result.get("condition") is None, (
            f"Hallucinated condition '{result.get('condition')}' — not in title"
        )

    def test_no_box_papers_when_absent(self, extractor):
        """No box/papers mentioned — should not infer."""
        result = extractor("Rolex Submariner 114060 No Date Black 40mm")
        assert result is not None
        assert result.get("box_papers") is None, (
            f"Hallucinated box_papers '{result.get('box_papers')}' — not in title"
        )

    def test_no_bezel_color_when_absent(self, extractor):
        """Generic title with no color — should not infer bezel color."""
        result = extractor("Rolex Submariner Date 40mm Automatic Watch")
        assert result is not None
        assert result.get("bezel_color") is None, (
            f"Hallucinated bezel_color '{result.get('bezel_color')}' — not in title"
        )

    def test_non_watch_listing_minimal_extraction(self, extractor):
        """Service listing — should extract almost nothing or return None."""
        result = extractor("Watch Service Repair & Full Polish - 1 Year Warranty For Rolex")
        # Returning None is fine — this isn't a product listing
        if result is not None:
            # If it returns something, it should not hallucinate a reference
            assert result.get("reference") is None, (
                f"Hallucinated reference '{result.get('reference')}' on a service listing"
            )

    def test_hulk_no_black_bezel(self, extractor):
        """Hulk is green — should not say black bezel."""
        result = extractor("Rolex Hulk Submariner Date Reference 116610LV")
        assert result is not None
        # "Hulk" implies green, but title doesn't say a color explicitly.
        # The key test: it should NOT say black.
        bezel = result.get("bezel_color")
        assert bezel != "black", (
            f"Said bezel_color='black' for Hulk — hallucinated wrong color"
        )

    def test_blue_dial_not_green(self, extractor):
        """Title says BLUE — should not also say green."""
        result = extractor("ROLEX SUBMARINER 40MM BLUE DIAL GOLD CLASP")
        assert result is not None
        assert result.get("dial_color") != "green"
        # Should not hallucinate a reference
        assert result.get("reference") is None, (
            f"Hallucinated reference '{result.get('reference')}' — not in title"
        )
