using System.Text.Json;
using AIOMarketMaker.ML.Utils;

namespace AIOMarketMaker.ML.Services;

public partial class LlmVariantClassifier
{
    private static readonly JsonSerializerOptions VerdictSerializerOptions = new()
    {
        Converters = { new CamelCaseEnumConverter() }
    };

    private static string SerializeVerdict(Verdict v) =>
        JsonSerializer.Serialize(v, VerdictSerializerOptions).Trim('"');

    private static readonly string Same = SerializeVerdict(Verdict.Same);
    private static readonly string Different = SerializeVerdict(Verdict.Different);
    private static readonly string Uncertain = SerializeVerdict(Verdict.Uncertain);

    internal static readonly string SystemPromptText = $"""
        Classify whether two eBay listings are comparable for pricing — would a buyer expect to pay roughly the same for both?

        STEP 1 — PRODUCT IDENTITY (reject if any apply)
        - Different product, model, or set. Match by product name, not category.
        - Modern reissue ≠ original. "Vintage Collection" (Hasbro) is a modern toy line, not an actual vintage item.
        - Accessory ≠ full product (e.g., "PS5 Disc Drive" ≠ "PS5 Console").
        - Different spec: storage, RAM, CPU, network lock status.
        - Different size, including shoe size and width (e.g., 12.5 4E ≠ 13 D).
        - Different quantity (single item ≠ lot or bundle of multiples).
        - Standard edition ≠ limited/special/collaboration edition.
        - Stock ≠ modified, customized, or engraved.

        STEP 2 — CONDITION (reject if 2+ bands apart)
        Bands: New/Sealed > Excellent > Good > Fair/Poor.
        Same or adjacent band → comparable. Two+ apart → not comparable.
        Sealed vs opened → always a gap (sealed = New, opened ≤ Good).
        If only one listing states condition, treat the unstated one as compatible.

        STEP 3 — COMPLETENESS (reject if included items differ)
        Compare what each listing includes: accessories, batteries, packaging, docs.
        - Genuine OEM batteries ≠ third-party batteries (different value and reliability).
        - Luxury items: box, papers, and certificates significantly affect price.
        - Jewelry: determine what's included from the TITLE, not the description. "Bracelet" ≠ "Bracelet With Charms."
        - Bundles: extras must be verifiably equivalent. Vague "+Extras" ≠ named specific items. If you cannot confirm contents match → "{Different}".

        STEP 4 — COLOR
        Non-fashion (electronics, furniture, appliances, doorbells, office chairs): color is always trivial, ignore it (e.g., carbon vs blue office chair → same).
        Fashion (sneakers, clothing, watches, jewelry): colorway matters.
        - Different named colorways → "{Different}", even if shades look similar.
        - One names a colorway, the other doesn't → "{Different}" (cannot confirm match).

        STEP 5 — SPARSE LISTINGS
        Missing detail ≠ difference. Only reject on explicit contradictions stated by BOTH listings.
        Titles are authoritative. eBay auto-generates descriptions that may be inaccurate — trust titles over descriptions.
        Trivial and always ignorable: manufacture year, seller location, box condition, included cables.

        OUTPUT: First give your reason (under 20 words), then set verdict.
        "{Same}" = comparable. "{Different}" = not comparable. "{Uncertain}" = cannot identify product.
        Apply each step independently — do not combine individually acceptable differences into a rejection.
        Default to "{Same}" when product identity matches and no explicit conflict exists.
        """;
}
