using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
[NonParallelizable]
public class LlmVariantClassifier_IntegrationTests
{
    private const string Model = "gpt-5-mini";

    private static LlmVariantClassifier _classifier = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var configPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "AIOMarketMaker.Etl", "local.settings.json");

        if (!File.Exists(configPath))
        {
            Assert.Ignore($"local.settings.json not found at {Path.GetFullPath(configPath)}");
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false)
            .Build();

        var apiKey = configuration.GetValue<string>("Values:OpenAi:ApiKey");
        if (string.IsNullOrEmpty(apiKey))
        {
            Assert.Ignore("OpenAi:ApiKey not found in local.settings.json (under Values)");
        }

        // gpt-5-mini doesn't support Temperature=0 (only default=1 allowed)
        var temperature = Model.StartsWith("gpt-5") ? (float?)null : 0f;
        var chatClient = new OpenAiChatClient(Model, apiKey, temperature);
        var config = new LlmClassifierConfig(MaxConcurrency: 5, MaxRetries: 3);
        _classifier = new LlmVariantClassifier(chatClient, config, Mock.Of<ILogger<LlmVariantClassifier>>());
    }

    // -----------------------------------------------------------------------
    // COMPARABLE — same product, similar condition, similar completeness
    // -----------------------------------------------------------------------

    [TestCaseSource(nameof(ComparablePairs))]
    public async Task Should_classify_as_comparable(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        TestContext.WriteLine($"[COMPARABLE] {description}: isComparable={result.IsComparable} reason={result.Reason}");
        Assert.That(result.IsComparable, Is.True, $"Expected COMPARABLE: {description}. LLM reason: {result.Reason}");
    }

    private static IEnumerable<TestCaseData> ComparablePairs()
    {
        // Electronics — same spec, same condition tier
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple iPhone 15 Pro Max 256GB Black Titanium Unlocked - Good Condition",
                "iPhone 15 Pro Max 256GB Black Titanium, unlocked, good condition with minor scratches on frame",
                "iPhone 15 Pro Max 256GB Black Titanium Unlocked Good Condition",
                "Good condition iPhone 15 Pro Max 256GB in black titanium, factory unlocked, light wear"),
            "iPhone 15 Pro Max 256GB — same spec, both Good condition"
        ).SetDescription("Same phone, same storage, same condition band");

        // Watches — same ref, both with box and papers
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Rolex Submariner Date 126610LN 41mm 2024 Box & Papers",
                "Rolex Submariner Date ref 126610LN, 41mm steel, black dial, 2024, complete set with box and papers",
                "Rolex 126610LN Submariner Date 41mm Black 2023 Full Set",
                "Rolex Submariner Date 126610LN, 41mm, black dial/bezel, 2023, full set with original box, warranty card"),
            "Rolex Submariner 126610LN — same ref, both full set, different year"
        ).SetDescription("Same Rolex reference, both complete sets");

        // Sneakers — same colorway, same size, both pre-owned
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nike Dunk Low Panda Black White UK 9 Pre-Owned",
                "Nike Dunk Low Panda colourway, UK 9, pre-owned with light creasing on toebox",
                "Nike Dunk Low Black White DD1391-100 UK9 Used",
                "Nike Dunk Low Panda DD1391-100, UK size 9, used, good condition, no box"),
            "Nike Dunk Low Panda UK9 — both pre-owned, same colorway"
        ).SetDescription("Same sneaker, same size, same condition tier");

        // Luxury fashion — same bag, both pre-owned excellent
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Louis Vuitton Neverfull MM Monogram Excellent Condition",
                "Louis Vuitton Neverfull MM in Monogram canvas with beige interior, excellent pre-owned condition",
                "LV Neverfull MM Monogram Canvas Beige Interior - Excellent",
                "Authentic Louis Vuitton Neverfull MM Monogram, beige lining, excellent condition with dust bag"),
            "LV Neverfull MM Monogram — both excellent condition"
        ).SetDescription("Same LV bag model and size, same condition");

        // Musical instruments — same model, both used
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Fender Player Stratocaster Maple Neck 3-Colour Sunburst Used",
                "Fender Player Stratocaster with maple neck, 3-colour sunburst finish, used, plays great",
                "Fender Player Series Strat Maple Fretboard Sunburst Pre-Owned",
                "Fender Player Stratocaster, maple fretboard, sunburst, pre-owned, all electronics working"),
            "Fender Player Strat Sunburst — both used, same spec"
        ).SetDescription("Same guitar model, colour variant, condition tier");

        // Home appliances — same model, both new
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "KitchenAid Artisan Stand Mixer 4.8L Empire Red - Brand New",
                "KitchenAid Artisan 4.8L Stand Mixer in Empire Red, brand new in box, 300W",
                "KitchenAid Artisan 4.8L Tilt-Head Stand Mixer Empire Red New Sealed",
                "Brand new sealed KitchenAid Artisan stand mixer 4.8L, Empire Red, full warranty"),
            "KitchenAid Artisan 4.8L Red — both brand new"
        ).SetDescription("Same appliance, same colour, both new");

        // Collectibles — same sealed product
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Pokemon Scarlet & Violet 151 Booster Box Sealed English",
                "Pokemon TCG Scarlet & Violet 151 booster box, factory sealed, English language",
                "Pokemon 151 SV Booster Box Factory Sealed English",
                "Sealed Pokemon Scarlet Violet 151 booster box, English, 36 packs"),
            "Pokemon 151 Booster Box — both factory sealed"
        ).SetDescription("Same collectible product, both sealed");

        // Vintage — same era, same size, same condition level
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Vintage Levis 501 W33 L32 Made in USA 1990s Medium Wash",
                "Authentic vintage Levi's 501 from the 1990s, medium stonewash, made in USA, button fly, good vintage condition",
                "Vintage Levis 501 W33 L32 MiUSA 90s Stonewash",
                "Vintage Levi's 501 jeans, 1990s, W33 L32, stonewash, USA made, typical vintage wear"),
            "Vintage Levis 501 W33 90s — same era/size/wash"
        ).SetDescription("Same vintage jeans, same size, same era");

        // Trivial color — office chair color doesn't affect pricing
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Herman Miller Aeron - Size B in Carbon - Good Condition",
                "Herman Miller Aeron Size B in carbon finish. Good condition, fully functioning with adjustable arms.",
                "Herman Miller Aeron Chair Size B Blue - Good Condition",
                "Herman Miller Aeron Size B in blue mesh. Good condition, fully functioning with adjustable arms."),
            "Herman Miller Aeron Size B — carbon vs blue, trivial color difference"
        ).SetDescription("Office chair color doesn't affect pricing — carbon vs blue");

        // Real DB: trivial color — doorbell finish doesn't affect pricing
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Ring Video Doorbell (2nd Gen) with Alexa - Satin Nickel (8VRDP7-0EU0)",
                "The Ring Video Doorbell (2nd Gen) in Satin Nickel. 155-degree view, connects wirelessly, powered by batteries, compatible with Amazon Alexa.",
                "Ring Video Doorbell (2nd Gen) 1080p HD Advanced Motion Detection-Venetian Bronze",
                "Brand new unused and unopened in original packaging. Ring Video Doorbell (2nd Generation) Wireless Doorbell - Venetian Bronze."),
            "Ring Doorbell 2nd Gen — Satin Nickel vs Venetian Bronze, trivial finish"
        ).SetDescription("Real DB pair: doorbell color finish is cosmetic, not price-relevant");

        // Real DB: adjacent condition — Good vs Fair is comparable per rules
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "The Beatles - Abbey Road LP | Apple Records | Used Vinyl | Good Condition",
                "Artist: The Beatles Album: Abbey Road Label: Apple Records Format: Vinyl LP Condition: Record: Good - light surface marks; Sleeve: Good - minor edge/ring wear",
                "The Beatles - Abbey Road LP Apple Records PCS 7088 1969 UK Pressing",
                "Play-graded and sounds VG. Laminated cover shows wear on corners and edges, spine. Tape repair on tears on front. The Beatles - Abbey Road Apple Records SO-383 (1971)"),
            "Abbey Road vinyl — Good vs Fair/VG condition, adjacent bands = comparable"
        ).SetDescription("Real DB pair: Good and Fair are adjacent condition bands");

        // Real DB: adjacent condition — Grade B/C vs minor bend, both in Good/Fair range
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple iPad Pro 256GB Wi-Fi 10.5in Space Grey iOS 17 Good Condition Grade B/C 895",
                "Apple iPad Pro 256GB Wi-Fi 10.5in Space Grey. BATTERY CAPACITY - 79%. Very light blemish on screen. Some wear, scratches and scuffs.",
                "2017 Apple iPad Pro 10.5\" 1st Gen (A1701)",
                "2017 iPad Pro first gen. Overall in pretty decent condition, minor bend on the chassis. No major scratches or cracks. 256gb storage. Comes with charging brick and cable."),
            "iPad Pro 10.5 256GB — Grade B/C vs minor bend, both Good/Fair band"
        ).SetDescription("Real DB pair: both in Good-Fair range, adjacent bands = comparable");

        // Real DB: same product with different listing detail levels
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Catan 5-6 Player Extension Seafarers Settlers of Catan Board Game  Expansion",
                "The Catan 5-6 Player Extension Seafarers is a strategy game by Catan that allows players to trade, build, and compete. Ages 12 and up.",
                "Catan Seafarers 5-6 Player Extension Expansion 3064 Sealed 2007 Kraus Teuber",
                "Strategy board game expansion pack from 2007. Based on the original The Settlers of Catan game."),
            "Catan Seafarers 5-6 Extension — same product, different listing detail"
        ).SetDescription("Real DB pair: same product with varying description detail");

        // Real DB: condition not stated — should not reject when one says New and other is silent
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Legend of Zelda: Tears of The Kingdom Nintendo Switch 2 Edition New and Sealed",
                "The Legend of Zelda: Tears of The Kingdom for the Nintendo Switch 2 Edition. This new and sealed game.",
                "Zelda: Tears of the Kingdom - Nintendo Switch 2 Edition",
                "physical copy switch 2 version."),
            "Zelda TOTK Switch 2 — one says New/Sealed, other silent on condition"
        ).SetDescription("Real DB pair: unstated condition should not create a 2-band gap with New");

        // Real DB: Nest thermostat — trivial accessory differences (cables, box, screws)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nest Learning Thermostat 2nd Generation Complete with Heat Link and Original Box",
                "Nest Learning Thermostat 2nd Generation. Nice condition. Comes complete with Heat Link, power cord, power plug, trim plate, screws, installation guide and original box.",
                "Google Nest Learning Thermostat (2nd Gen) inc. Heatlink and Thermostat Base",
                "Google Nest Learning Thermostat (2nd Gen) inc. Heatlink and Thermostat Base. Used but in great condition. Also includes stand for thermostat."),
            "Nest Thermostat 2nd Gen — both complete, different trivial accessories (cables/box/stand)"
        ).SetDescription("Real DB pair: cables, box, screws are trivial per prompt — core product identical");

        // Sonos One SL — Good w/ power cable vs Excellent w/ original packaging
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Sonos ONE SL White - 003",
                "Sonos One SL wireless speaker. Good condition with only a few minor scratches and no dents. Fully functional and comes with a power cable. Same day shipping.",
                "Sonos One SL Wireless Speaker - Excellent Condition",
                "Excellent condition. It has only ever been moved/touched when unboxing/boxing. Comes in original packaging with all cables that came with it."),
            "Sonos One SL — cables and box are trivial per Step 5"
        ).SetDescription("Real DB pair: cables, original packaging are trivial — both are same Sonos One SL");

        // WiFi Video Doorbell — one includes receiver, other doesn't mention
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Wireless Smart Video Doorbell WiFi Security Camera Bell Phone Door Ring intercom",
                "High-quality ABS material. Ultra-high-definition camera lens and a 125 degree wide-angle lens. Built-in two-way microphone.",
                "HD Video Doorbell Camera Smart WiFi Wireless Ring Bell with Receiver UK",
                "Smart Video Doorbell Camera Wireless Indoor Outdoor Surveillance AI-Powered Human Detection. Secure Cloud Storage."),
            "WiFi Video Doorbell — missing detail (receiver) is not an explicit contradiction"
        ).SetDescription("Real DB pair: per Step 5, missing detail != difference — only reject on explicit contradictions");

        // PlayStation Portal — White vs Midnight Black
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "PlayStation Portal PS5",
                "Selling this as never really used. Bought for my son Christmas 2025. Turned on once. As new condition. White, 1TB storage.",
                "PS Portal",
                "Barely used Sony PlayStation Portal in Midnight Black, excellent condition. Complete with original box. No charger included."),
            "PlayStation Portal — White vs Midnight Black, electronics color trivial"
        ).SetDescription("Real DB pair: per Step 4, non-fashion electronics color is always trivial");
    }

    // -----------------------------------------------------------------------
    // NOT COMPARABLE — condition gap (strict band matching)
    // -----------------------------------------------------------------------

    [TestCaseSource(nameof(ConditionGapPairs))]
    public async Task Should_reject_when_condition_gap_too_large(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.That(result.IsComparable, Is.False, $"Expected NOT COMPARABLE (condition gap): {description}");
        TestContext.WriteLine($"[CONDITION GAP] {description}: isComparable={result.IsComparable}");
    }

    private static IEnumerable<TestCaseData> ConditionGapPairs()
    {
        // New/Sealed vs Fair/Poor — 2+ bands apart
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple iPhone 15 Pro Max 256GB Black Titanium - Brand New Sealed",
                "Brand new factory sealed iPhone 15 Pro Max 256GB Black Titanium with full Apple warranty",
                "iPhone 15 Pro Max 256GB Black Titanium - Cracked Back, Grade C",
                "iPhone 15 Pro Max 256GB, cracked rear glass, heavy scratches on screen, Grade C, fully functional"),
            "iPhone 15 Pro Max 256GB — New/Sealed vs Grade C cracked"
        ).SetDescription("Same phone but extreme condition gap");

        // Excellent vs For Parts
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Sony PlayStation 5 Disc Edition Console - Excellent Condition",
                "PS5 disc edition in excellent condition, fully working, includes controller and cables",
                "Sony PlayStation 5 Disc Edition - FOR PARTS NOT WORKING",
                "PS5 disc edition, does not power on, sold as parts only, no returns"),
            "PS5 Disc Edition — Excellent vs For Parts/Not Working"
        ).SetDescription("Working console vs parts-only");

        // New vs Well-Used (watches — huge condition premium)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Rolex Submariner 126610LN Unworn 2024 Stickers Full Set",
                "Unworn Rolex Submariner 126610LN with factory stickers still attached, 2024, complete box and papers",
                "Rolex Submariner 126610LN Heavily Worn Polished No Box",
                "Rolex Submariner 126610LN, heavily worn with desk-diving marks, case has been polished, no box or papers"),
            "Rolex 126610LN — Unworn with stickers vs heavily worn/polished"
        ).SetDescription("Same ref, extreme condition gap for luxury watch");

        // Grade A vs Grade C (electronics refurb market)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "MacBook Pro 14-inch M3 Pro 18GB 512GB Space Black - Grade A Pristine",
                "MacBook Pro M3 Pro, Grade A pristine condition, no marks or scratches, battery cycle count 12",
                "MacBook Pro 14\" M3 Pro 18GB 512GB Space Black - Grade C Fair",
                "MacBook Pro M3 Pro, Grade C condition, dents on lid, scratched screen, worn keyboard, battery 78% health"),
            "MacBook Pro M3 Pro — Grade A pristine vs Grade C with dents"
        ).SetDescription("Same laptop, graded condition gap");

        // Mint/Like New vs Damaged (luxury fashion)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Louis Vuitton Neverfull MM Monogram - Like New with Receipt",
                "LV Neverfull MM Monogram, like new condition, used twice, includes original receipt from LV store",
                "Louis Vuitton Neverfull MM Monogram - Stained, Worn Handles",
                "LV Neverfull MM Monogram, handles darkened and cracking, interior staining, zipper pull missing"),
            "LV Neverfull MM — Like New vs heavily worn/stained"
        ).SetDescription("Same bag, condition gap affects price 40%+");

        // Sealed vs Opened (collectibles — massive price impact)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Pokemon Scarlet & Violet 151 Booster Box Factory Sealed",
                "Pokemon 151 booster box, factory sealed with Pokémon Company shrink wrap intact",
                "Pokemon Scarlet & Violet 151 Booster Box OPENED 30 Packs Remaining",
                "Pokemon 151 booster box, opened, 30 of 36 packs remaining, 6 packs searched"),
            "Pokemon 151 Booster Box — sealed vs opened/searched"
        ).SetDescription("Sealed vs opened collectible, huge value difference");
    }

    // -----------------------------------------------------------------------
    // NOT COMPARABLE — bundle / completeness mismatch
    // -----------------------------------------------------------------------

    [TestCaseSource(nameof(BundleMismatchPairs))]
    public async Task Should_reject_when_bundle_mismatch(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        TestContext.WriteLine($"[BUNDLE] {description}: isComparable={result.IsComparable}, reason={result.Reason}");
        Assert.That(result.IsComparable, Is.False, $"Expected NOT COMPARABLE (bundle): {description}");
    }

    private static IEnumerable<TestCaseData> BundleMismatchPairs()
    {
        // Tablet only vs tablet + accessories bundle
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple iPad Pro 11-inch M4 256GB WiFi Space Black - Tablet Only",
                "iPad Pro 11 M4 256GB WiFi, tablet only, no box or accessories",
                "Apple iPad Pro 11\" M4 256GB WiFi + Magic Keyboard + Apple Pencil Pro Bundle",
                "iPad Pro 11-inch M4 256GB WiFi with Apple Magic Keyboard Folio and Apple Pencil Pro, all original boxes"),
            "iPad Pro M4 — tablet only vs with Magic Keyboard + Pencil Pro bundle"
        ).SetDescription("£500+ worth of accessories changes the price");

        // Camera body only vs lens kit
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Canon EOS R6 Mark II Mirrorless Camera Body Only",
                "Canon EOS R6 II body only, 24.2MP full frame, excellent condition, shutter count 5000",
                "Canon EOS R6 Mark II + RF 24-105mm f/4L IS USM Lens Kit",
                "Canon EOS R6 II with RF 24-105mm f/4L IS USM lens, complete kit, low shutter count"),
            "Canon R6 II — body only vs with 24-105mm L lens kit"
        ).SetDescription("Lens kit adds £1000+ to price");

        // Console vs console + games bundle
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Sony PlayStation 5 Slim Disc Edition 1TB Console",
                "PS5 Slim disc edition 1TB, includes controller and HDMI cable",
                "PS5 Slim Disc Edition 1TB Bundle + 2 Controllers + 5 Games",
                "PS5 Slim 1TB with 2 DualSense controllers, Spider-Man 2, FC 25, Hogwarts Legacy, God of War, GT7"),
            "PS5 Slim — console only vs bundle with extra controller and 5 games"
        ).SetDescription("Extra controller + games significantly inflate price");

        // Watch with full set vs no box/papers
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Omega Seamaster 300M Blue 210.30.42.20.03.001 Full Set Box Papers 2024",
                "Omega Seamaster Diver 300M blue dial, full set with inner/outer box, warranty card, pictogram, 2024",
                "Omega Seamaster 300M Blue 210.30.42.20.03.001 Watch Only No Box",
                "Omega Seamaster 300M blue dial, watch only, no box, no papers, no accessories"),
            "Omega Seamaster 300M — full set with box/papers vs watch only"
        ).SetDescription("Box and papers add 10-20% on luxury watches");

        // Real DB: both Zelda editions but different bundle contents (ONNX wrongly says comparable)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo Switch OLED Legend of Zelda: Tears of the Kingdom Edition, 64gb+Extras",
                "Selling this Nintendo Switch Oled in great condition with games and extras. Original Box Included.",
                "Nintendo Switch OLED Console - Zelda + pro controller + Japanese Games Bundle",
                "The Nintendo Switch OLED - The Legend of Zelda - Tears of the Kingdom edit bundle is a special edition handheld system."),
            "Switch OLED Zelda — both bundles but different extras (games vs pro controller)"
        ).SetDescription("Real DB pair: ONNX wrongly matched bundles with different contents");

        // Real DB: vague "with Games" bundle vs plain console
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo Switch OLED 64GB Excellent Condition with Games",
                "The Nintendo Switch OLED 64GB is a handheld gaming system from Nintendo, released in 2021. This sleek white console features a 7-inch OLED display.",
                "Nintendo Switch OLED 64GB White",
                "Immerse yourself in gaming excellence with the Nintendo Switch OLED 64GB, a sleek white handheld system that offers a vibrant OLED screen."),
            "Switch OLED — vague 'with Games' bundle vs plain console"
        ).SetDescription("Real DB pair: unspecified games = vague extras, can't confirm contents match");

        // Real DB: same tool but different included batteries (non-genuine vs genuine)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Milwaukee M18BLID2-0 Brushless 1/4in Hex Impact Driver",
                "Milwaukee Impact Driver. Comes with 2 batteries that are not genuine milwaukee but work fine. No charger either but one battery has charge so you can see it working.",
                "Milwaukee M18 BLID2 Impact Driver w049000173841",
                "Milwaukee M18 BLID2 Impact Driver. Used condition, marks and signs of wear from previous use. Cosmetic only, does not affect performance. Included: Milwaukee M18 BLID2 Impact Driver, Milwaukee M18 5.0Ah battery."),
            "Milwaukee M18 — 2x non-genuine batteries no charger vs genuine 5.0Ah battery"
        ).SetDescription("Real DB pair: different included batteries and charger presence");

        // Real DB: plain bracelet vs bracelet loaded with charms
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Sterling Silver 925 Bracelet Pandora Hallmarked Pre-owned",
                "Pre-owned Pandora bracelet crafted from sterling silver with a purity of 925. Features a round charm style with a heart theme.",
                "Pandora S925 Ale Silver Bracelet 17cm With Charms And Safety Chain",
                "Pandora S925 Ale Silver Bracelet featuring sterling silver chain charms and safety chain. Hallmarked and signed by Pandora."),
            "Pandora bracelet — plain vs with charms and safety chain"
        ).SetDescription("Real DB pair: charms add significant value, different completeness");

        // Switch OLED full console vs Screen Only
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "NINTENDO Switch OLED - Neon Red and Blue - REFURB-C",
                "NINTENDO Switch OLED - Neon Red and Blue - REFURB-C",
                "Nintendo Switch OLED Edition 64GB Games Console - Screen Only (U)",
                "Nintendo Switch OLED Edition 64GB Games Console - Screen Only (U). Pre-owned."),
            "Switch OLED — full console vs Screen Only (missing Joy-Cons/dock)"
        ).SetDescription("Real DB pair: Screen Only = accessory, not full product per Step 1");
    }

    // -----------------------------------------------------------------------
    // NOT COMPARABLE — different variant (should already work with current prompt)
    // -----------------------------------------------------------------------

    [TestCaseSource(nameof(DifferentVariantPairs))]
    public async Task Should_reject_different_variant(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        TestContext.WriteLine($"[VARIANT] {description}: isComparable={result.IsComparable}, reason={result.Reason}");
        Assert.That(result.IsComparable, Is.False, $"Expected NOT COMPARABLE (variant): {description}");
    }

    private static IEnumerable<TestCaseData> DifferentVariantPairs()
    {
        // Different storage
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Samsung Galaxy S24 Ultra 256GB Titanium Black",
                "Samsung Galaxy S24 Ultra 256GB in titanium black",
                "Samsung Galaxy S24 Ultra 512GB Titanium Black",
                "Samsung Galaxy S24 Ultra 512GB in titanium black"),
            "Galaxy S24 Ultra 256GB vs 512GB — different storage"
        ).SetDescription("Same phone, different storage tier");

        // Different CPU/chip tier
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple MacBook Pro 14\" M3 Pro 18GB 512GB",
                "MacBook Pro 14-inch with M3 Pro chip, 18GB unified memory, 512GB SSD",
                "Apple MacBook Pro 14\" M3 Max 36GB 1TB",
                "MacBook Pro 14-inch with M3 Max chip, 36GB unified memory, 1TB SSD"),
            "MacBook Pro M3 Pro vs M3 Max — different chip tier"
        ).SetDescription("Same form factor, completely different performance tier");

        // Accessory vs full product
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "PS5 Disc Drive for PlayStation 5 Digital Edition CFI-ZDD1",
                "Official Sony PS5 Disc Drive attachment for Digital Edition, model CFI-ZDD1",
                "Sony PlayStation 5 Slim Disc Edition 1TB Console CFI-2015",
                "PS5 Slim disc edition 1TB console with DualSense controller"),
            "PS5 Disc Drive accessory vs PS5 Console — completely different products"
        ).SetDescription("Accessory matched to full product");

        // Different generation (vintage)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Vintage Levis 501 Big E Selvedge 1960s W32 L30 Redline",
                "Rare 1960s Levi's 501 Big E with selvedge denim and redline pocket, single stitch",
                "Levis 501 Original Fit W32 L30 2024 Dark Wash",
                "Brand new Levi's 501 Original Fit jeans, 2024 production, dark wash, W32 L30"),
            "Levis 501 1960s Big E vintage vs 2024 modern production — different era/value"
        ).SetDescription("Same model name but decades apart, 10-50x value difference");

        // Different watch reference (subtle)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Rolex Submariner Date 126610LN Black Dial 41mm",
                "Rolex Submariner Date ref 126610LN, black dial and bezel, 41mm, stainless steel",
                "Rolex Submariner Date 126610LV Green Dial 41mm Kermit",
                "Rolex Submariner Date ref 126610LV, green dial and bezel, 41mm, stainless steel, Kermit"),
            "Rolex Submariner 126610LN (black) vs 126610LV (green) — different reference"
        ).SetDescription("Different Rolex references despite similar names");

        // Locked vs unlocked phone
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "iPhone 15 Pro Max 256GB Black Titanium - EE Network Locked",
                "iPhone 15 Pro Max 256GB, locked to EE network, cannot be used on other carriers",
                "iPhone 15 Pro Max 256GB Black Titanium - Factory Unlocked",
                "iPhone 15 Pro Max 256GB, factory unlocked, works on any network worldwide"),
            "iPhone 15 Pro Max — network locked vs factory unlocked"
        ).SetDescription("Carrier-locked phones sell for 20-30% less");

        // Real DB: different colorway (ONNX wrongly says comparable)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Adidas Yeezy Boost 350 V2 Butter - 8.5 Men's UK - Excellent Condition",
                "Adidas Yeezy Boost 350 V2 in men's UK size 8.5. Excellent Condition - very light usage with original box",
                "Adidas Yeezy Boost 350 V2 'Cream' UK 8.5 USED Condition Next Day Ship",
                "Adidas Yeezy Boost 350 V2 'Cream' UK 8.5. Used condition, judge photos. No box included"),
            "Yeezy 350 V2 Butter vs Cream — different colorway, different resale value"
        ).SetDescription("Real DB pair: ONNX wrongly matched different Yeezy colorways");

        // Real DB: different product package (ONNX wrongly says comparable)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dyson Airwrap Multi-Styler Long Edition",
                "The Dyson Airwrap Multi-Styler Long Edition is a high-powered electric hair styling device.",
                "Dyson Airwrap Multi-Styler Complete Long",
                "The Dyson Airwrap Multi-Styler Complete Long is a travel-sized electric hair styling device designed for women."),
            "Dyson Airwrap Long Edition vs Complete Long — different SKU/package"
        ).SetDescription("Real DB pair: ONNX wrongly matched different Dyson Airwrap packages");

        // Real DB: different collectible sets — must catch different product names
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "MTG Magic: The Gathering TCG - Lorwyn Eclipsed - Collector Booster Box",
                "Brand New - Factory Sealed Box. Stock In-Hand. Will Ship Straight Away.",
                "MTG - CMM - Commander Masters Collector Booster Box New & Sealed English",
                "Factory sealed. Can provide more pics upon request."),
            "MTG Lorwyn Eclipsed vs Commander Masters — completely different sets"
        ).SetDescription("Real DB pair: LLM wrongly matched different MTG sets based on shared 'booster box' keywords");

        // Real DB: modern Hasbro reissue vs original 1980 Kenner figure (10x+ value difference)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Star Wars Vintage Collection PRINCESS LEIA (Bespin Escape) Action Figure",
                "The Star Wars Vintage Collection Princess Leia (Bespin Escape) Action Figure is a highly detailed and collectible toy by Hasbro.",
                "Vintage Star Wars Figure 1980 Princess Leia Bespin/Gown.100% Original Complete",
                "Vintage Star Wars Figure Princess Leia Bespin-Gown 1980. 100% Original Complete. Very good condition with minimal wear, limbs still stiff. Original cape, blaster."),
            "Star Wars Vintage Collection (modern Hasbro) vs 1980 Kenner original — different products"
        ).SetDescription("Real DB pair: 'Vintage Collection' is a modern reissue line, not the same as actual vintage");

        // Real DB: sneaker with specific named colorway vs generic listing (fashion — colorway matters)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nike Air Jordan 1 Mid GS Size 5.5UK Pinksicle Safety Orange White DX3240 681 EUC",
                "These used Nike Air Jordan 1 Mid GS in size 5.5 including a spare pair of orange laces combines style and performance with its pink, safety orange, and white colorway.",
                "Air Jordan 1 MID (GS)",
                "Air Jordan 1 MID (GS). Condition is New with box. Dispatched with Royal Mail Tracked 48."),
            "AJ1 Mid GS Pinksicle colorway vs generic AJ1 Mid GS — unknown colorway match"
        ).SetDescription("Real DB pair: sneakers are fashion, can't confirm colorway match when one is generic");

        // Real DB: different named boot colorways (fashion — colorway matters)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dr. Martens Pink Leather Boots Women Size UK 5 Patent DM 1460 Doc Hot Lace Up",
                "Acid Pink Dr. Martens Boots UK size 5 Good Condition: see pictures for an overview",
                "Dr. Martens Women's Fuchsia Boots 1460 W",
                "Bright hot pink patent leather DMs. Signs of wear on top. Some cracking and scuffs. Soles in great condition. Reflected in price. Idea boots to re spray."),
            "Dr Martens 1460 Acid Pink vs Fuchsia — different named colorways on fashion boots"
        ).SetDescription("Real DB pair: different named colorways on fashion items are different products");

        // Real DB: different shoe size AND width (fashion — size matters)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Men's New Balance 990v6 Gray M990GL6 Made in USA Castlerock Grey Size 12.5 4E",
                "Would grade condition 7.5 out of 10",
                "New Balance 990v6 Made in USA Castlerock Gray Running Shoes Men's Sz 13 D",
                "No holes or rips. Tread with light wear."),
            "NB 990v6 Size 12.5 4E (extra wide) vs Size 13 D (standard) — different size and width"
        ).SetDescription("Real DB pair: different sizes and widths are not comparable for shoes");

        // Real DB: Hermès Birkin 25 different colorways (luxury fashion — colorway matters hugely)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Hermes Birkin 25 handbag bg28331",
                "Brand: Hermes. Stamp: B stamp (made in 2023). Color: Tundra. Material: Togo. Size: W 25cm x H 21cm x D 13cm. Accessories: Box, storage bag, key, padlock, crochet.",
                "HERMES Birkin 25 Veau Epsom Vert Bengale Hand Bag Purse 90306364",
                "Brand Name: HERMES. Material: Veau Epsom. Color: Vert Bengale. Country: France. Size: W 9.84 inch, H 8.07 inch, D 5.31 inch."),
            "Hermès Birkin 25 Tundra vs Vert Bengale — different color AND material on luxury"
        ).SetDescription("Real DB pair: Birkin colorways have wildly different resale values");

        // Real DB: Birkenstock different material (Birko-Flor synthetic vs genuine leather)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Birkenstock Arizona Birko-Flor Taupe Sandals Size 38 / US L 7 Narrow",
                "Shoes are in used condition. The straps have some scuffs and scratches. Interior footbeds have dark staining and wear.",
                "Birkenstock Arizona Sandals Shoes Black Leather 38 L 7 Soft Footbed Narrow 1124",
                "BIRKENSTOCK SANDALS LEATHER SIZE 38 WOMEN 7. 100% AUTHENTIC OR YOUR MONEY BACK"),
            "Birkenstock Arizona Taupe Birko-Flor vs Black Leather — different material AND color"
        ).SetDescription("Real DB pair: Birko-Flor (synthetic) vs leather is a real product/material difference");

        // Yeezy 350 V1 Black vs 350 V2 Hyperspace — different model + colorway
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "adidas Yeezy Boost YZY 350 Men's UK Size 10",
                "The adidas Yeezy Boost YZY 350 Men's UK Size 10 is a stylish low-top trainer. Black sneaker from the adidas Yeezy product line with rubber outsole.",
                "Adidas Yeezy Boost 350 V2 \"Hyperspace\" Men's Size 10 Trainers (EG7491)",
                "Adidas Yeezy Boost 350 V2 \"Hyperspace\" - Asia-exclusive release in men's UK 10. Light grey/green Primeknit upper. Clean pre-owned condition. No box - shoes only."),
            "Yeezy 350 black vs 350 V2 Hyperspace — different model AND colorway"
        ).SetDescription("Real DB pair: V1 vs V2 is a different model, plus different named colorway on fashion");

        // Brompton M6L Black Edition Electric vs M6L Electric — special edition
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Brompton M6l Black Edition 6 Speed 2020",
                "Brompton electric Black Edition 6 Speed 2020. Complete with Brompton charger and the practical removable battery in a Brompton bag, 250w motor.",
                "Brompton M6L Electric Folding Bike 2021",
                "Condition: Excellent. Pre-Owned Used. Brand: Brompton. Model: M6L Electric. Year: 2021. Colour: Bolt Blue. Weight: 17.34 kg."),
            "Brompton M6L Black Edition vs standard M6L Electric — special edition"
        ).SetDescription("Real DB pair: Black Edition is a special/limited edition per Step 1");

        // Dr Martens 1460 8-eye vs 7-eye steel toe — entirely different product
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Vintage Dr. Martens 1460 Made in England Black Leather Boots UK 8 | 8 Eye",
                "Vintage Dr. Martens 1460 Made in England Black Leather Boots UK 8. 8 Eye. Excellent condition for the age. All stitching is solid.",
                "Dr Martens Vintage 90s Made in England Black Steel Toe 7 eye size UK 8 (434)",
                "Dr Martens Vintage 90s Made in England Black Steel Toe boots Size UK 8. 7 eyelets. Steel toe. Genuine Leather. Excellent vintage condition."),
            "Dr Martens 1460 8-eye vs 7-eye steel toe — different product model and spec"
        ).SetDescription("Real DB pair: 1460 is specifically 8-eye; 7-eye steel toe is a different model");

        // Dyson Airwrap Complete Long vs Special Edition
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dyson Airwrap Multi-Styler Long Complete Edition Blue And Pink",
                "Dyson Airwrap Multi-Styler Long Complete Edition in Blue and Pink. Ceramic heating plate and three temperature settings up to 150C. Comes with attachments shown.",
                "Dyson Airwrap Complete Long Special Edition Hair Multi-Styler Set - Vinca Blue",
                "Dyson Airwrap Complete Long Special Edition in Vinca Blue. Brand new, never used; comes complete with all attachments, styling comb and storage box. Maximum temperature 150C."),
            "Dyson Airwrap Complete Long standard vs Special Edition"
        ).SetDescription("Real DB pair: standard edition vs Special Edition per Step 1");
    }

    // -----------------------------------------------------------------------
    // NOT COMPARABLE — quantity mismatch
    // -----------------------------------------------------------------------

    [TestCaseSource(nameof(QuantityMismatchPairs))]
    public async Task Should_reject_quantity_mismatch(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.That(result.IsComparable, Is.False, $"Expected NOT COMPARABLE (quantity): {description}");
        TestContext.WriteLine($"[QUANTITY] {description}: isComparable={result.IsComparable}");
    }

    private static IEnumerable<TestCaseData> QuantityMismatchPairs()
    {
        // Single item vs lot
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Titleist Pro V1 Golf Balls - Sleeve of 3",
                "Titleist Pro V1 golf balls, sleeve of 3 balls, brand new",
                "Titleist Pro V1 Golf Balls - 4 Dozen (48 Balls) BULK",
                "48x Titleist Pro V1 golf balls, 4 dozen, bulk lot, mixed years"),
            "Titleist Pro V1 — 3 balls vs 48 ball bulk lot"
        ).SetDescription("Radically different quantities");

        // Single booster pack vs sealed box
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Pokemon 151 Single Booster Pack",
                "Single Pokemon Scarlet & Violet 151 booster pack, sealed, from booster box",
                "Pokemon 151 Booster Box 36 Packs Sealed",
                "Pokemon Scarlet & Violet 151 sealed booster box containing 36 booster packs"),
            "Pokemon 151 — single pack vs sealed box of 36"
        ).SetDescription("Single pack vs full box");

        // Wholesale job lot
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Funko Pop Marvel Spider-Man #593",
                "Funko Pop Marvel Spider-Man No Way Home #593, new in box",
                "JOBLOT 20x Funko Pop Figures Marvel DC Mixed Bundle",
                "Job lot of 20 Funko Pop figures, mix of Marvel and DC, various conditions, sold as bundle"),
            "Single Funko Pop vs job lot of 20 mixed figures"
        ).SetDescription("Single collectible vs wholesale lot");
    }

    // -----------------------------------------------------------------------
    // NOT COMPARABLE — modifications / special editions
    // -----------------------------------------------------------------------

    [TestCaseSource(nameof(ModificationPairs))]
    public async Task Should_reject_modified_or_special_edition(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.That(result.IsComparable, Is.False, $"Expected NOT COMPARABLE (modification): {description}");
        TestContext.WriteLine($"[MODIFICATION] {description}: isComparable={result.IsComparable}");
    }

    private static IEnumerable<TestCaseData> ModificationPairs()
    {
        // Stock vs modified guitar
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Fender American Professional II Stratocaster Maple Dark Night",
                "Fender American Professional II Strat, all original, maple neck, Dark Night finish, V-Mod II pickups",
                "Fender American Professional II Stratocaster MODDED Custom Shop Pickups Locking Tuners",
                "Fender Am Pro II Strat heavily modified: Custom Shop Fat 50s pickups, Gotoh locking tuners, Graphtech nut, rewired"),
            "Fender Am Pro II Strat — stock vs heavily modified"
        ).SetDescription("Mods change character and value of instrument");

        // Factory original vs aftermarket parts (watches)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Rolex Datejust 126234 Silver Dial Jubilee 36mm Factory Original",
                "Rolex Datejust 126234, factory silver dial, jubilee bracelet, all original parts, 36mm",
                "Rolex Datejust 126234 36mm Aftermarket Diamond Dial Custom Bezel",
                "Rolex Datejust 126234 with aftermarket diamond-set dial and custom diamond bezel, non-original parts"),
            "Rolex Datejust — factory original vs aftermarket diamond customisation"
        ).SetDescription("Aftermarket modifications reduce value for serious collectors");

        // Standard vs special edition console
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo Switch OLED Model White Joy-Con Console",
                "Nintendo Switch OLED model with white Joy-Con controllers, standard edition",
                "Nintendo Switch OLED The Legend of Zelda Tears of the Kingdom Edition",
                "Limited edition Nintendo Switch OLED Tears of the Kingdom, unique design with Hylian artwork on dock and Joy-Cons"),
            "Switch OLED standard White vs Zelda TOTK special edition"
        ).SetDescription("Special edition commands premium over standard");

        // Personalised/engraved item
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple AirPods Pro 2nd Generation USB-C MagSafe Case",
                "Apple AirPods Pro 2 with USB-C MagSafe charging case, brand new",
                "Apple AirPods Pro 2nd Gen USB-C ENGRAVED 'Happy Birthday Sarah'",
                "AirPods Pro 2 USB-C with custom engraving 'Happy Birthday Sarah' on case, brand new, non-returnable"),
            "AirPods Pro 2 — standard vs personalised engraving"
        ).SetDescription("Engraving reduces resale value");

        // Real DB: stock PS5 vs custom hand-painted console
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "SONY PlayStation 5 PS5 825GB Disk Edition * 100% Fully working * FREE Next Day",
                "Sony PlayStation 5 Disk Edition 825GB Blu-Ray. Boxed. Fully working. Comes with official controller and all wires. Restored to factory settings.",
                "Sony PS5 825GB Blu-Ray Disc Edition Console Custom Black - Console Only tested",
                "Selling a Sony PS5 825GB Blu-Ray Disc Edition console with custom hand-painted black side panels. This listing is for the console only plus power supply - no controller, no box."),
            "PS5 Disc — stock boxed vs custom hand-painted, console only"
        ).SetDescription("Real DB pair: custom paint = modified, plus missing controller/box");
    }

    // -----------------------------------------------------------------------
    // COMPARABLE — sparse titles with minimal detail (smoke test regressions)
    // -----------------------------------------------------------------------

    [TestCaseSource(nameof(SparseComparablePairs))]
    public async Task Should_classify_sparse_titles_as_comparable(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.That(result.IsComparable, Is.True, $"Expected COMPARABLE (sparse): {description}");
        TestContext.WriteLine($"[SPARSE-COMPARABLE] {description}: isComparable={result.IsComparable}");
    }

    private static IEnumerable<TestCaseData> SparseComparablePairs()
    {
        // Sparse title with no condition — should assume Good, not infer New or different
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo Switch OLED Model HEG-001 Handheld Console - 64GB - White",
                "Nintendo Switch OLED, white Joy-Cons, 64GB, good condition",
                "Nintendo Switch OLED",
                ""),
            "Switch OLED verbose vs sparse title — both default to Good condition"
        ).SetDescription("Sparse title should not be assumed New or different condition");

        // Both sparse, same product
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "AirPods Pro 2nd Generation",
                "",
                "airpods pro 2nd generation",
                ""),
            "AirPods Pro 2 — both sparse titles, same product"
        ).SetDescription("Identical sparse titles should be comparable, not uncertain");

        // One with model numbers, one without — same product
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Samsung Galaxy Tab S9 FE 128GB Grey",
                "Samsung Galaxy Tab S9 FE, 128GB storage, grey colour, good condition",
                "samsung galaxy tab S9 FE 128GB",
                ""),
            "Galaxy Tab S9 FE 128GB — detailed vs sparse, same product"
        ).SetDescription("Should not hallucinate specs not mentioned in either listing");
    }

    // -----------------------------------------------------------------------
    // NOT COMPARABLE — platform/generation differences (smoke test findings)
    // -----------------------------------------------------------------------

    [TestCaseSource(nameof(PlatformDifferencePairs))]
    public async Task Should_reject_platform_difference(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.That(result.IsComparable, Is.False, $"Expected NOT COMPARABLE (platform): {description}");
        TestContext.WriteLine($"[PLATFORM] {description}: isComparable={result.IsComparable}");
    }

    private static IEnumerable<TestCaseData> PlatformDifferencePairs()
    {
        // Switch 2 Edition vs standard Switch — different platforms
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "The Legend of Zelda: Tears of the Kingdom - Switch 2 Edition (Nintendo Switch 2)",
                "Zelda TOTK for Nintendo Switch 2, new platform release",
                "The Legend of Zelda: Tears of the Kingdom Nintendo Switch (New)",
                "Zelda Tears of the Kingdom for original Nintendo Switch, brand new sealed"),
            "Zelda TOTK Switch 2 Edition vs original Switch — different platforms"
        ).SetDescription("Different console generations, different product");

        // PS5 game vs PS4 game — same title, different platform
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Spider-Man 2 PS5 Disc Brand New Sealed",
                "Marvel's Spider-Man 2 for PlayStation 5, brand new factory sealed",
                "Spider-Man 2 PS4 Disc Brand New Sealed",
                "Marvel's Spider-Man 2 for PlayStation 4, brand new factory sealed"),
            "Spider-Man 2 PS5 vs PS4 — same game, different console"
        ).SetDescription("Same game title but different console platform");
    }

    // -----------------------------------------------------------------------
    // Cross-category sanity check — obviously different products
    // -----------------------------------------------------------------------

    [TestCaseSource(nameof(CrossCategoryPairs))]
    public async Task Should_reject_cross_category(ClassifyPairRequest pair, string description)
    {
        var results = await _classifier.Classify([pair]);
        var result = results[0];

        Assert.That(result.IsComparable, Is.False, $"Expected NOT COMPARABLE (cross-category): {description}");
        TestContext.WriteLine($"[CROSS-CAT] {description}: isComparable={result.IsComparable}");
    }

    private static IEnumerable<TestCaseData> CrossCategoryPairs()
    {
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dyson V15 Detect Absolute Cordless Vacuum",
                "Cordless vacuum with laser dust detection",
                "Dyson Airwrap Complete Long Styler",
                "Hair styling tool with multiple attachments"),
            "Dyson vacuum vs Dyson hair styler"
        ).SetDescription("Same brand, completely different products");

        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple Watch Ultra 2 49mm Titanium GPS+Cellular",
                "Apple Watch Ultra 2, 49mm titanium case, GPS+Cellular",
                "Apple Watch SE 2nd Gen 40mm Aluminum GPS",
                "Apple Watch SE 2nd generation, 40mm aluminum, GPS only"),
            "Apple Watch Ultra 2 vs SE — different product line"
        ).SetDescription("Same brand, different product tier and hardware");
    }

    // -----------------------------------------------------------------------
    // DIAGNOSTIC — real DB pairs through C# classifier for manual review
    // Run with: dotnet test --filter "Should_classify_real_db_pairs"
    // Not for CI — used to spot-check LLM vs ONNX on real data.
    // -----------------------------------------------------------------------

    [Test]
    [Explicit("Manual diagnostic — not for CI")]
    public async Task Should_classify_real_db_pairs()
    {
        var pairs = RealDbPairs().ToList();
        var requests = pairs.Select(p => p.Arguments[0] as ClassifyPairRequest).ToList();

        var results = await _classifier.Classify(requests!);

        for (var i = 0; i < pairs.Count; i++)
        {
            var onnxLabel = (string)pairs[i].Arguments[1]!;
            var category = (string)pairs[i].Arguments[2]!;
            var llmLabel = results[i].IsComparable ? "same" : "different";
            var marker = llmLabel == onnxLabel ? " OK" : "!!!";
            var confidence = results[i].Confidence;

            TestContext.WriteLine($"[{marker}] ONNX={onnxLabel,-9} LLM={llmLabel,-9} conf={confidence:F1} | {category}");
            TestContext.WriteLine($"      A: {requests[i]!.TitleA}");
            TestContext.WriteLine($"      B: {requests[i]!.TitleB}");
            if (results[i].Reason is { } reason)
            {
                TestContext.WriteLine($"      Reason: {reason}");
            }
            TestContext.WriteLine();
        }

        var agree = results.Select((r, i) =>
        {
            var onnx = (string)pairs[i].Arguments[1]!;
            var llm = r.IsComparable ? "same" : "different";
            return llm == onnx;
        }).Count(x => x);

        TestContext.WriteLine($"Agreement: {agree}/{results.Count} ({agree * 100 / results.Count}%)");
    }

    private static IEnumerable<TestCaseData> RealDbPairs()
    {
        // === COMPARABLE (ONNX=same) — 25 pairs ===

        // 1. North Face Nuptse — both XL, one black, one black/olive green
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Vintage The North Face nuptse 700 winter down puffer jacket black XL",
                "-Size- XL -P2P- 26.5\" -Length- 28.5\" -Condition- 8.5/10 -Details- -Black colour way -The North Face nuptse 700 -Warm winter puffer jacket",
                "The North Face Nuptse 700 Down Puffer Jacket Mens XL Black Olive Green Retro 90s",
                "The North Face Retro 90s Nuptse 700 Down Jacket Men's Size XL Good CONDITION see pics Very small repair see pics Hidden hood in collar 100% Authentic"),
            "same", "The North Face Nuptse Jacket");

        // 2. Logitech MX Master 3S — both same mouse, one new, one generic
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Logitech MX Master 3S Bluetooth 8K DPI Wireless Performance Mouse - Gray - New",
                "Logitech's MX Master 3S mouse has been remastered for even greater tactility, performance, and flow.",
                "Logitech MX Master 3S - Performance Wireless Mouse with Ultra-fast...",
                "Logitech MX Master 3S - Performance Wireless Mouse with Ultra-fast Scrolling, Ergo, 8K DPI, Track on Glass, Quiet Clicks, Bluetooth, Windows, Linux, Chrome - Graphite"),
            "same", "Logitech MX Master 3");

        // 3. Beatles Abbey Road — one US pressing, one UK pressing
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "The Beatles - Abbey Road LP | Apple Records | Used Vinyl | Good Condition",
                "Artist: The Beatles Album: Abbey Road Label: Apple Records Format: Vinyl LP Condition: Record: Good - light surface marks; Sleeve: Good - minor edge/ring wear",
                "The Beatles - Abbey Road LP Apple Records PCS 7088 1969 UK Pressing",
                "Play-graded and sounds VG. Laminated cover shows wear on corners and edges, spine. Tape repair on tears on front. The Beatles - Abbey Road Apple Records SO-383 (1971)"),
            "same", "Abby Road Vinyl");

        // 4. Zelda TOTK Switch 2 Edition — both same platform edition
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Legend of Zelda: Tears of The Kingdom Nintendo Switch 2 Edition New and Sealed",
                "The product is the Legend of Zelda: Tears of The Kingdom for the Nintendo Switch 2 Edition. This new and sealed game is part of the iconic video game series.",
                "Zelda: Tears of the Kingdom - Nintendo Switch 2 Edition",
                "physical copy switch 2 version. Will also come with \u00a330 of next order deal to. Great deal"),
            "same", "Zelda Tears of the Kingdom");

        // 5. Dyson Airwrap — Complete Set Long vs Long Edition
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Excellent Condition Dyson Airwrap Multi-Styler Complete Set Long Topaz Orange",
                "Girlfriend bought this in 2023 but barely uses it as it doesn't work particularly well with her hair type. Very well looked after and includes all original parts.",
                "Dyson Airwrap Multi-Styler Long Edition",
                "Opened but never used orange airwrap. The Dyson Airwrap Multi-Styler Long Edition is a hair styling device suitable for women. 150\u00b0C max, ceramic heating plates, 3 temperature settings."),
            "same", "Dyson Airwrap");

        // 6. AirPods Pro 2 — both genuine, both Lightning MagSafe
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Genuine Apple AirPods Pro 2nd Gen Noise Cancelling",
                "Used, excellent working condition. 100% genuine as come with Apple limited warranty until November 2025! A replacement charging cable is included!",
                "Genuine Apple AirPods Pro (2nd generation) with MagSafe Lightning charging case",
                "Apple AirPods Pro (2nd Generation) with MagSafe Lightning wireless charging case. Well looked-after, in excellent condition and full working order."),
            "same", "AirPods Pro 2");

        // 7. Rolex Submariner 126610LV — both unworn, both full set
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Rolex Submariner 126610LV \"Starbucks\" - 2025 - Unworn - Full Set - New Box",
                "Unworn 2025 Rolex Submariner Date 126610LV - the highly sought-after \"Starbucks\" (green bezel / black dial). Full UK Set. New Rolex Green Eco Box.",
                "Rolex Submariner Date 126610LV 2024 Unworn Box And Papers Starbucks MK2 Kermit",
                "Rolex Submariner Date 126610LV 41mm Starbucks Kermit 2024 Box And Papers. Complete with box and papers. 100% authentic."),
            "same", "Rolex Submariner");

        // 8. MacBook Pro M3 Pro 18GB 1TB — Space Black vs Space Grey
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple MacBook Pro 14-inch in M3 Pro Space Black 18GB RAM, 1TB SSD, 2023 Week Use",
                "Only 1 week used Apple MacBook Pro 14-inch in M3 Pro Space Black boasts impressive specifications, with 18GB of RAM, a 1TB SSD, and a powerful Apple M3 Pro processor.",
                "Apple MacBook Pro 14-inch Laptop 1TB SSD, M3 Pro, 18GB RAM, Space Grey",
                "Have not used this laptop for over a year, bought in 2023 and then moved to a Microsoft in 6 months. Still practically new."),
            "same", "MacBook Pro M3");

        // 9. Air fryer silicone liners — both 2-pack for Ninja
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "2PCS Air Fryer Silicone Liners for Ninja Double Stack 7.6L Air Fryer SL300UK, Du",
                "Compatible with Ninja double stack air fryer SL400UK, AF400UK, AF451UK and Tower T17088. Holes designed for better circulation of hot air.",
                "2 Pack Air Fryer Silicone liner for Ninja Dual Air Fryer Reusable Air Fryer",
                "Silicone air fryer basket compatible with Ninja double stack AF400UK AF300UK Tower T170889.5L, Air Fryer Accessories for Keplin Instant Vortex"),
            "same", "Ninja Foodi Air Fryer");

        // 10. Sigma 50mm F1.4 EX DG HSM for Canon EF
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "\"Excellent+++\" Sigma 50mm F1.4 EX DG HSM for Canon EF from Japan #3476",
                "Grade: Excellent+++ This item is clean. There are some scuffs and scratches on the body. It works properly. No scratches on optical system.",
                "Sigma 50mm F1.4 EX DG HSM Prime Lens for Canon EF",
                "Sigma EX 50mm F/1.4 HSM DG Lens Canon EF fit. Comes with original front and rear lens caps. In perfect working order."),
            "same", "Canon RF 50mm Lens");

        // 11. Dyson Airwrap Complete Long — same product name both sides
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dyson Airwrap Complete Long",
                "Used only once this Dyson Airwrap Complete Long is a stylish and powerful electric multi-styler designed for women. 150\u00b0C max, ceramic heating plates.",
                "Dyson AirwrapT Multi-Styler Complete Long Edition",
                "The Dyson Airwrap Multi-Styler Complete Long Edition is a powerful electric hair styling device designed specifically for long hair. 150\u00b0C max, ceramic, 3 temperature settings."),
            "same", "Dyson Airwrap");

        // 12. AirPods Pro 2nd Gen — both MagSafe Lightning
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple AirPods Pro 2nd Generation with MagSafe Charging Case Lightning",
                "Apple AirPods Pro 2nd Generation with MagSafe Charging Case Lightning",
                "Apple Airpods Pro (2nd generation)! magsafe case.",
                "apple airpods pro 2nd generation magsafe case. Dispatched with Evri Tracked."),
            "same", "AirPods Pro 2");

        // 13. Canon EF 50mm f/1.4 USM — Mint vs Used
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Canon EF 50mm f/1.4 USM Lens - Mint Condition - Boxed + Filter - Hardly Used",
                "Canon EF 50mm f/1.4 USM lens, in mint condition and hardly used. Fantastic fast prime lens. Dorr DHG Super Protect 58mm filter fitted from new.",
                "Canon EF 50mm f/1.4 USM Lens | Used but Well Cared For",
                "Canon EF 50mm f/1.4 USM Lens, a fast and versatile prime lens. Used but well cared for, only being sold as I'm reorganising my kit."),
            "same", "Canon RF 50mm Lens");

        // 14. Samsung Galaxy Tab S9 128GB WiFi — Graphite vs Beige Grade B
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Samsung Galaxy Tab S9 SM-X710 128GB, Wi-Fi, 11\" - Graphite",
                "Samsung Galaxy Tab S9 (SM-S710) - 128GB, Graphite, with Two S-Pens (No Charger). Premium Android tablet with bright AMOLED display, smooth performance.",
                "Samsung Galaxy Tab S9 SM-X710 128GB WiFi 11\" AMOLED Android Tablet Beige Grade B",
                "Samsung Galaxy Tab S9 SM-X710. Featuring an 11\" Dynamic AMOLED display with 120Hz refresh rate. Powered by Qualcomm Snapdragon 8 Gen 2."),
            "same", "Samsung Galaxy Tab S9");

        // 15. Ubiquiti UAP-AC-PRO — without bracket vs with wall plate no adapter
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Ubiquiti Networks UAP-AC-PRO UniFi WiFi Access Point Without Bracket",
                "POW WAP-Used and in perfectly working condition without brackets",
                "Ubiquiti UniFi UAP-AC-PRO PoE Wireless Access Point with Wall Plate No Adapter",
                "VGC, DOESNT INCLUDE THE GIGABIT POE ADAPTER!"),
            "same", "Ubiquiti UniFi Access Point");

        // 16. Herman Miller Aeron Size B
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Herman Miller Aeron - Size B in carbon",
                "The Herman Miller Aeron in Size B in carbon is a premium office chair designed for comfort and functionality. The carbon colour adds a modern touch.",
                "Herman Miller Aeron Chair Size B",
                "Fully functioning, includes adjustable arms. Has a scratch on one leg (shown in picture). Seat and backrest in blue. In good usable condition."),
            "same", "Herman Miller Aeron Chair");

        // 17. Catan Seafarers 5-6 Player Extension
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Catan 5-6 Player Extension Seafarers Settlers of Catan Board Game  Expansion",
                "The Catan 5-6 Player Extension Seafarers is a strategy game by Catan that allows players to trade, build, and compete. Ages 12 and up.",
                "Catan Seafarers 5-6 Player Extension Expansion 3064 Sealed 2007 Kraus Teuber",
                "Strategy board game expansion pack from 2007. Based on the original The Settlers of Catan game."),
            "same", "Settlers of Catan Board Game");

        // 18. Ray-Ban Meta Wayfarer Gen 2 — Shiny Black Green vs Matte Black Clear-to-Grey
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Ray-Ban Meta Wayfarer (Gen 2) Shiny Black Transitions Graph, Green Lenses S50",
                "Ray-Ban Meta Wayfarer (Gen 2) Sunglasses in Shiny Black Transitions with Green Lenses. Square style frame made from plastic, UV400 protection.",
                "Ray-Ban Meta Wayfarer Gen 2 Matte Black Clear to Grey Transition Lens",
                "Brand New Meta Quest Wayfarer (Gen 2) Matte black Large. Clear to Grey transitions glasses"),
            "same", "Ray-Ban Wayfarer Sunglasses");

        // 19. Canon EF 50mm f/2.5 Compact Macro — one good, one AS IS with focusing issues
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Canon EF 50mm f/2.5 Compact Macro",
                "Canon EF 50mm f/2.5 Compact Macro Grade Good. Focal length display window has scratches and dust. Internal dust: Some. Haze: None. Scratches: None. Fungus: Visible to the naked eye.",
                "AS IS Canon EF 50mm F/2.5 Compact Macro Lens From Japan",
                "AS IS. This lens has focusing issues, so even when you take pictures, they don't come into focus and end up blurry."),
            "same", "Canon RF 50mm Lens");

        // 20. iRobot Roomba 660 — identical titles
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "iRobot Roomba Vacuum Cleaner Robot 660",
                "The iRobot Roomba Vacuum Cleaner Robot 660 is a cordless robotic vacuum cleaner. Quick charge time of only 3 hours.",
                "iRobot Roomba Vacuum Cleaner Robot 660",
                "The iRobot Roomba Vacuum Cleaner Robot 660 is a cutting-edge home appliance. Battery operation, sleek design."),
            "same", "iRobot Roomba");

        // 21. Harry Potter Chamber of Secrets — USA Edition Unread vs 1st Print First US Edition
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Harry Potter and the Chamber of Secrets USA Edition UNREAD",
                "Harry Potter and the Chamber of Secrets USA Edition 1999 Hardback. FIRST PRINT, FIRST EDITION UNREAD. Printed in the USA by Scholastic Press. Always kept in a protective cover, as new.",
                "Harry Potter and the Chamber of Secrets Hardcover 1st Print First US Edition",
                "Book in very good condition. All printing errors included. Likely a BCE. No writing or highlighting. Clean pages. Full Number Line."),
            "same", "First Edition Harry Potter");

        // 22. Ring Video Doorbell 2nd Gen — Satin Nickel vs Venetian Bronze
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Ring Video Doorbell (2nd Gen) with Alexa - Satin Nickel (8VRDP7-0EU0)",
                "The Ring Video Doorbell (2nd Gen) in Satin Nickel. 155-degree view, connects wirelessly, powered by batteries, compatible with Amazon Alexa.",
                "Ring Video Doorbell (2nd Gen) 1080p HD Advanced Motion Detection-Venetian Bronze",
                "Brand new unused and unopened in original packaging. Purchased from Screwfix but never used. Ring Video Doorbell (2nd Generation) Wireless Doorbell - Venetian Bronze."),
            "same", "Ring Video Doorbell");

        // 23. Callaway Paradym Ai Smoke Max 9 Driver — standard vs Tour Issue
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Callaway Paradym Ai Smoke Max 9\u00b0 Driver / Stiff Tensei AV Blue 65 S Shaft",
                "Dexterity: Right Hand. Club Type: Driver. Loft: 9. Shaft: Mitsubishi Tensei AV Blue 65 S. Flex: Stiff. Grip: Golf Pride Tour Velvet 360 Standard. Condition Head: 9/10.",
                "Tour Issue Callaway Paradym Ai Smoke Max 9\u00b0 Driver / Tensei AV Blue 65 S Shaft",
                "Dexterity: Right Hand. Loft: 9. Shaft: Tensei AV Blue 65 S. Flex: Stiff. Grip: Golf Pride Tour Velvet Jumbo (Logo Down). Notes: Serial number indicates club is tour issue. Head: 7/10."),
            "same", "Callaway Paradym Driver");

        // 24. Nintendo Switch OLED — verbose with box vs sparse title
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo switch OLED Model, excellent condition, with box and accessories",
                "The Nintendo Switch OLED Model is a handheld gaming system by Nintendo. Sleek white design, 64GB of storage capacity, 1080p (FHD), Wi-Fi, charger, HDMI connectivity.",
                "Nintendo Switch OLED Model",
                "The Nintendo Switch OLED Model is a handheld gaming system by Nintendo, featuring a sleek white design. 64GB of storage capacity, Wi-Fi."),
            "same", "Nintendo Switch OLED");

        // 25. iPad Pro 10.5 256GB — Grade B/C vs one with minor bend
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple iPad Pro 256GB Wi-Fi 10.5in Space Grey iOS 17 Good Condition Grade B/C 895",
                "Apple iPad Pro 256GB Wi-Fi 10.5in Space Grey. BATTERY CAPACITY - 79%. Very light blemish on screen. Some wear, scratches and scuffs.",
                "2017 Apple iPad Pro 10.5\" 1st Gen (A1701)",
                "2017 iPad Pro first gen. Overall in pretty decent condition, minor bend on the chassis. No major scratches or cracks. 256gb storage. Comes with charging brick and cable."),
            "same", "iPad Pro");

        // === NOT COMPARABLE (ONNX=different) — 25 pairs ===

        // 26. Air Jordan 1 Mid GS — specific colorway used vs generic new with box
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nike Air Jordan 1 Mid GS Size 5.5UK Pinksicle Safety Orange White DX3240 681 EUC",
                "These used Nike Air Jordan 1 Mid GS in size 5.5 including a spare pair of orange laces combines style and performance with its pink, safety orange, and white colorway.",
                "Air Jordan 1 MID (GS)",
                "Air Jordan 1 MID (GS). Condition is New with box. Dispatched with Royal Mail Tracked 48."),
            "different", "Nike Air Jordan 1");

        // 27. iPad Pro M5 — 13\" 256GB vs 11\" 1TB
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple iPad Pro 13-Inch 2025 With M5, 256GB WiFi & 5G Cellular Unlocked - Black",
                "This is a gift which is still in its original unopened shipping box.",
                "ipad pro m5 11 inch 1TB - Brand New & Unopened",
                "ipad pro m5 11 inch 1TB - Brand New & Unopened"),
            "different", "iPad Pro");

        // 28. Signed Messi Barcelona Shirt — 2005/06 CL Final vs 2014/15 Third Kit framed
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Barcelona 2005/06 Champions League Final Shirt Signed By LIONEL MESSI beckett",
                "2005/06 Barcelona Home shirt signed by LIONEL MESSI! Signed at a private signing with Messi, fully certified by Beckett. Number 30 which is Messi's debut number.",
                "Signed Lionel Messi Barcelona Shirt Beckett QR Coa Framed Jersey",
                "Lionel Messi Signed 2014/15 FC Barcelona Third Kit - Beckett COA - Professionally Framed. Own a true piece of football history."),
            "different", "Signed Football Shirt");

        // 29. Switch OLED White + extras (bundle) vs Black/Neon Red
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo Switch OLED Model HEG-001 Handheld Console - 64GB - White + extras",
                "Nintendo Switch OLED White + extras. Comes boxed with charger, hdmi cable. Also comes with nitrodeck. Also comes with game mario rabbids. Mario wonder has been redeemed.",
                "Nintendo Switch OLED Model HEG-001 Handheld Console - 64GB - Black/Neon Red/",
                "Very good condition, fully tested and working. Comes with usb c cable to charge"),
            "different", "Nintendo Switch OLED");

        // 30. Cartier Love Bracelet — Size 18 vs Size 16
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Pre-Owned Cartier Love Bracelet - Size 18 Rose Gold",
                "Pre-Owned Cartier Love Bracelet - Size 18 Rose Gold. Certified Pre-Owned.",
                "Pre-Owned Cartier Love Bracelet - Size 16 Rose Gold",
                "Pre-Owned Cartier Love Bracelet - Size 16 Rose Gold. Certified Pre-Owned."),
            "different", "Cartier Love Bracelet");

        // 31. Star Wars Vintage Collection (modern) vs actual 1980 Kenner figure
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Star Wars Vintage Collection PRINCESS LEIA (Bespin Escape) Action Figure",
                "The Star Wars Vintage Collection Princess Leia (Bespin Escape) Action Figure is a highly detailed and collectible toy by Hasbro.",
                "Vintage Star Wars Figure 1980 Princess Leia Bespin/Gown.100% Original Complete",
                "Vintage Star Wars Figure Princess Leia Bespin-Gown 1980. 100% Original Complete. Very good condition with minimal wear, limbs still stiff. Original cape, blaster."),
            "different", "Vintage Star Wars Figure");

        // 32. MacBook Pro 16\" M3 Pro vs MacBook Air 15\" M3
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple MacBook Pro 16\" 2023 M3 Pro, 512GB SSD, 18GB RAM, AppleCare+ 13/03/27",
                "Nearly New Apple Macbook Pro 16\". Basically new considering the use it has had. Included are original box and power adapter, both dark and transparent protective case.",
                "MacBook Air 15 Inch 2024 M3 16gb Ram 512gb SSD 100% Battery Excellent (5837",
                "Apple MacBook Air Blue 2023 15\" M3 16gb ram 512gb SSD. Hardly been used and in excellent condition. Battery cycle count is only 88 - 100% capacity."),
            "different", "MacBook Pro M3");

        // 33. Google Nest Heat Link 3rd Gen vs Nest Thermostat E
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Google Nest Heat Link 3rd Generation - Working - Genuine",
                "The Google Nest Heat Link 3rd Generation is a white wireless thermostat controlled via the Nest app. Powered by a 24V corded electric source.",
                "Google Nest Thermostat E With Heat Link E - Save on Energy with Smart Home",
                "Google Smart Thermostat E 3rd Gen with Heat Link E. Programmable smart thermostat that learns your schedule and the temperatures you like."),
            "different", "Nest Thermostat");

        // 34. Harry Potter CoS 1st Ed 32nd Print vs TRUE 1st/1st UK
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Harry Potter and The Chamber of Secrets by JK Rowling (1st Edition, 32nd Print)",
                "First edition, 32nd print paperback copy of Harry Potter and the Chamber of Secrets. Published by Bloomsbury. Written in English.",
                "JK Rowling Harry Potter Chamber of Secrets TRUE 1st Edition (UK) 4.99 BLOOMSBURY",
                "Beautiful 1st Edition, 1st Printing (U.K.) of Harry Potter and the Chamber of Secrets. Original 1st printing U.K. paperback published by Bloomsbury in 1998. BOOK: NEAR FINE. Printed price of 4.99."),
            "different", "First Edition Harry Potter");

        // 35. LG OLED TV vs Samsung QLED TV
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "LG Smart TV OLED55C54LA (2025) 55\" OLED HDR 4K Ultra HD Silver C Grade",
                "LG Smart TV OLED55C54LA (2025) 55\" OLED HDR 4K Ultra HD Silver C Grade. Refurbished with 12 month guarantee, full working condition.",
                "Samsung 55 inch Smart TV QE55QN80F 2025 Neo QLED Mini LED HDR 4K Ultra HD Black",
                "Samsung 55 inch Smart TV QE55QN80F 2025 Neo QLED Mini LED HDR 4K Ultra HD Black. Refurbished with 12 month guarantee."),
            "different", "LG C3 OLED TV");

        // 36. Herman Miller Aeron Size B vs Size C
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Herman Miller Aeron Office Chair Lumbar | Size B | Graphite Black | Fully Loaded",
                "Herman Miller Aeron Office Chair. Excellent condition, fully working. Size B. Colour: Graphite Black. RRP \u00a31150. Features: forward & backward tilt, height adjustable arms.",
                "Herman Miller Aeron Chair Size C Model BRAND NEW - Local Delivery",
                "Herman Miller Aeron Chair. Latest Remastered Model - BRAND NEW! Size C - LARGE! SL PostureFit. Height adjustable arms. New and unused."),
            "different", "Herman Miller Aeron Chair");

        // 37. ASUS ROG Strix B450-F vs X570-I — different chipset/form factor
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "ASUS ROG Strix B450-F Gaming Socket AM4 DDR4 ATX Motherboard",
                "The ASUS ROG Strix B450-F Gaming motherboard is designed for gaming enthusiasts. ATX form factor, AMD CPUs, four memory slots DDR4 SDRAM.",
                "For ASUS ROG STRIX X570-I GAMING motherboard AM4 DDR4",
                "ASUS ROG STRIX X570-I GAMING motherboard for high-performance gaming on AMD Ryzen. AM4 socket, DDR4 memory support. Selling due to an upgrade, tested and all ports working."),
            "different", "ASUS ROG Strix Motherboard");

        // 38. Stihl chainsaw spark plug vs chainsaw chain — different accessories
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Spark Plug NGK BPMR7A Fits Stihl Chainsaw MS 361 362 390 391 440 441 460 461",
                "",
                "Pack of 2 Redpart 12\" Chainsaw Chain Stihl - Chainsaws 009, 010, 011, 012, 017",
                ""),
            "different", "Stihl Chainsaw");

        // 39. Rolex Submariner 16613 (40mm, older) vs 116613LB (newer reference)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Rolex Submariner Date 40mm BLUE Two-Tone 18K Yellow Gold GOLD BUCKLE 16613",
                "Rolex Submariner Date 40mm BLUE Two-Tone. Reference Number 16613. Serial Number F-Series. Two-Tone (18K Yellow Gold / Stainless Steel). Case Size 40mm.",
                "ROLEX Submariner Date 116613LB Bluesy Bi-Metal Watch 2018 Box & Papers",
                "Rolex Submariner Date 116613LB Bluesy. REFERENCE: 116613LB. YEAR: 2018. BOX: Yes. PAPERS: Yes. MOVEMENT: Automatic. CASE SIZE: 40mm. BEZEL: Blue Ceramic."),
            "different", "Rolex Submariner");

        // 40. MTG Lorwyn Eclipsed vs Commander Masters — different sets
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "MTG Magic: The Gathering TCG - Lorwyn Eclipsed - Collector Booster Box",
                "Brand New - Factory Sealed Box. Stock In-Hand. Will Ship Straight Away.",
                "MTG - CMM - Commander Masters Collector Booster Box New & Sealed English",
                "Factory sealed. Can provide more pics upon request."),
            "different", "MTG Magic The Gathering Booster Box");

        // 41. Milwaukee M18 BLID2 — both same model, different included batteries
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Milwaukee M18BLID2-0 Brushless 1/4in Hex Impact Driver",
                "Milwaukee Impact Driver. Comes with 2 batteries that are not genuine milwaukee but work fine. No charger either but one battery has charge so you can see it working.",
                "Milwaukee M18 BLID2 Impact Driver w049000173841",
                "Milwaukee M18 BLID2 Impact Driver. Used condition, marks and signs of wear from previous use. Cosmetic only, does not affect performance. Included: Milwaukee M18 BLID2 Impact Driver, Milwaukee M18 5.0Ah battery."),
            "different", "Milwaukee M18 Impact Driver");

        // 42. Dr Martens 1460 Pink Patent vs Fuchsia
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dr. Martens Pink Leather Boots Women Size UK 5 Patent DM 1460 Doc Hot Lace Up",
                "Acid Pink Dr. Martens Boots UK size 5 Good Condition: see pictures for an overview",
                "Dr. Martens Women's Fuchsia Boots 1460 W",
                "Bright hot pink patent leather DMs. Signs of wear on top. Some cracking and scuffs. Soles in great condition. Reflected in price. Idea boots to re spray."),
            "different", "Dr Martens 1460 Boots");

        // 43. Yamaha P-115 vs P-225 — different models
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Yamaha Digital Piano P-115 with Gig bag, stand, stool & sustain pedal.",
                "This piano has 88 GHS weighted and graded keys and uses Yamaha's Pure CF sound engine. Built in are 14 voices, 10 styles, 14 rhythms.",
                "Yamaha P-225 Portable Digital Piano",
                "P-225 Portable Digital Piano. Yamaha's P Series digital pianos. Comfortable feel of an acoustic piano in an innovative, compact design."),
            "different", "Yamaha P-125 Digital Piano");

        // 44. Tiffany Elsa Peretti Teardrop vs Double Teardrop
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Tiffany & Co. Elsa Peretti Teardrop Pendant Necklace - Sterling Silver, 16\" |||",
                "Classic and effortlessly elegant Tiffany & Co. Teardrop pendant necklace by Elsa Peretti. Crafted from sterling silver, the fluid teardrop silhouette.",
                "Tiffany & Co. Elsa Peretti Double Teardrop Pendant Necklace $290 Retail",
                "Tiffany & Co. Elsa Peretti Double Teardrop Pendant Necklace. 100% Authentic. Necklace & Pendant are in used condition. Necklace is 16\" in Length. The pendant is 13mm."),
            "different", "Tiffany Elsa Peretti Necklace");

        // 45. Pandora plain bracelet vs bracelet with charms
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Sterling Silver 925 Bracelet Pandora Hallmarked Pre-owned",
                "Pre-owned Pandora bracelet crafted from sterling silver with a purity of 925. Features a round charm style with a heart theme.",
                "Pandora S925 Ale Silver Bracelet 17cm With Charms And Safety Chain",
                "Pandora S925 Ale Silver Bracelet featuring sterling silver chain charms and safety chain. Hallmarked and signed by Pandora."),
            "different", "Pandora Charm Bracelet");

        // 46. Air Jordan 1 Mid Speckle Size 13 vs Triple Black Size 11
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Size 13 - Air Jordan 1 Mid Speckle",
                "Air Jordan 1 Mid Speckle - Size 13. Only been tried on, ensuring they're in excellent condition. The box does have a minor dent.",
                "Nike Air Jordan 1 Mid Triple Black Size 11 - Look At Pictures For Condition",
                "Nike Air Jordan 1 Mid Triple Black 554724-030 Men's Basketball Sneakers in size 11. Mid-top silhouette and leather upper material."),
            "different", "Nike Air Jordan 1");

        // 47. New Balance 990v6 Size 12.5 4E vs Size 13 D
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Men's New Balance 990v6 Gray M990GL6 Made in USA Castlerock Grey Size 12.5 4E",
                "Would grade condition 7.5 out of 10",
                "New Balance 990v6 Made in USA Castlerock Gray Running Shoes Men's Sz 13 D",
                "No holes or rips. Tread with light wear."),
            "different", "New Balance 990v6");

        // 48. Generic impact wrench for DeWalt battery vs combi hammer drill for DeWalt
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "800Nm 1/2\"Cordless Electric Impact Wrench Drill Driver For Dewalt 18 20V Battery",
                "Impact Driver: Compatible with Dewalt 18V lithium-ion batteries such as DCB180 DCB182 DCB184. (Body Only) Efficient BL Brushless Motor.",
                "13MM Chuck Brushless Cordless Combi Hammer Drill for Dewalt 18V 20V Battery UK",
                "13MM Chuck Brushless Cordless Combi Hammer Drill for Dewalt 18V 20V Battery UK. Compatible with Dewalt 18/20V MAX lithium-ion batteries. (Battery not included.)"),
            "different", "DeWalt Cordless Drill");

        // 49. Air Jordan 1 Retro Low OG Size 9 vs High Size 11.5
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Air Jordan 1 Retro Low OG Obsidian UNC Men's Shoes CZ0790-400 Size 9",
                "Excellent condition. Minimal wear. Comes with OG box. Will ship ASAP!",
                "Size 11.5 - Nike Air Jordan 1 Retro OG High 'Obsidian UNC' 2019 GREAT CONDITION",
                "100% authentic! eBay authenticity guarantee! GREAT CONDITION Original box included! Open to offers!"),
            "different", "Nike Air Jordan 1");

        // 50. Ray-Ban RB2140 Wayfarer vs RB4194 Highstreet — different models
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Ray-Ban RB2140 902/57 Wayfarer Tortoise Brown 50mm Polarized Lens Sunglasses",
                "Ray-Ban RB2140 902/57 Wayfarer Sunglasses. Classic and iconic. Brown tortoise frame, polarized glass lenses. Made in Italy. UV400 protection.",
                "RAYBAN HIGHSTREET RECTANGULAR RB4194 6032/85 53 WAYFARER SUNGLASSES",
                "Rayban Highstreet Rectangular RB4194 6032/85 53 Wayfarer Sunglasses. Classic brown plastic frame, UV400 protection. Rectangular shape."),
            "different", "Ray-Ban Wayfarer Sunglasses");
    }

    // -----------------------------------------------------------------------
    // DIAGNOSTIC BATCH 2 — 30 new random DB pairs
    // Run with: dotnet test --filter "Should_classify_real_db_pairs_batch2"
    // -----------------------------------------------------------------------

    [Test]
    [Explicit("Manual diagnostic — not for CI")]
    public async Task Should_classify_real_db_pairs_batch2()
    {
        var pairs = RealDbPairsBatch2().ToList();
        var requests = pairs.Select(p => p.Arguments[0] as ClassifyPairRequest).ToList();

        var results = await _classifier.Classify(requests!);

        for (var i = 0; i < pairs.Count; i++)
        {
            var onnxLabel = (string)pairs[i].Arguments[1]!;
            var category = (string)pairs[i].Arguments[2]!;
            var llmLabel = results[i].IsComparable ? "same" : "different";
            var marker = llmLabel == onnxLabel ? " OK" : "!!!";
            var confidence = results[i].Confidence;

            TestContext.WriteLine($"[{marker}] ONNX={onnxLabel,-9} LLM={llmLabel,-9} conf={confidence:F1} | {category}");
            TestContext.WriteLine($"      A: {requests[i]!.TitleA}");
            TestContext.WriteLine($"      B: {requests[i]!.TitleB}");
            if (results[i].Reason is { } reason)
            {
                TestContext.WriteLine($"      Reason: {reason}");
            }
            TestContext.WriteLine();
        }

        var agree = results.Select((r, i) =>
        {
            var onnx = (string)pairs[i].Arguments[1]!;
            var llm = r.IsComparable ? "same" : "different";
            return llm == onnx;
        }).Count(x => x);

        TestContext.WriteLine($"Agreement: {agree}/{results.Count} ({agree * 100 / results.Count}%)");
    }

    private static IEnumerable<TestCaseData> RealDbPairsBatch2()
    {
        // === COMPARABLE (ONNX=same) — 15 pairs ===

        // 1. Khans of Tarkir Booster Box — both sealed
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Khans of Tarkir English Booster Box Sealed Mtg Magic the Gathering 36 Packs",
                "The Khans of Tarkir English Booster Box is a sealed pack containing 36 packs of Magic: The Gathering cards.",
                "MTG Magic Khans Of Tarkir Factory Sealed Booster Box English MTG FREE ship 2014",
                "Email with any questions. If there is a quantity of one on the listing, then you are purchasing the exact item in the pictures."),
            "same", "MTG Magic The Gathering Booster Box");

        // 2. Hermes Birkin 25 — Tundra vs Vert Bengale (different colors on luxury)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Hermes Birkin 25 handbag bg28331",
                "Brand: Hermes. Stamp: B stamp (made in 2023). Color: Tundra. Material: Togo. Size: W 25cm x H 21cm x D 13cm. Accessories: Box, storage bag, key, padlock, crochet.",
                "HERMES Birkin 25 Veau Epsom Vert Bengale Hand Bag Purse 90306364",
                "Brand Name: HERMES. Material: Veau Epsom. Color: Vert Bengale. Country: France. Size: W 9.84 inch, H 8.07 inch, D 5.31 inch."),
            "same", "Hermes Birkin Bag");

        // 3. Birkenstock Arizona 38 — Taupe vs Black
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Birkenstock Arizona Birko-Flor Taupe Sandals Size 38 / US L 7 Narrow",
                "Shoes are in used condition. The straps have some scuffs and scratches. Interior footbeds have dark staining and wear.",
                "Birkenstock Arizona Sandals Shoes Black Leather 38 L 7 Soft Footbed Narrow 1124",
                "BIRKENSTOCK SANDALS LEATHER SIZE 38 WOMEN 7. 100% AUTHENTIC OR YOUR MONEY BACK"),
            "same", "Birkenstock Arizona Sandals");

        // 4. Nest Learning Thermostat 2nd Gen — both complete
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nest Learning Thermostat 2nd Generation Complete with Heat Link and Original Box",
                "Nest Learning Thermostat 2nd Generation. Nice condition. Comes complete with Heat Link, power cord, power plug, trim plate, screws, installation guide and original box.",
                "Google Nest Learning Thermostat (2nd Gen) inc. Heatlink and Thermostat Base",
                "Google Nest Learning Thermostat (2nd Gen) inc. Heatlink and Thermostat Base. Used but in great condition. Also includes stand for thermostat."),
            "same", "Nest Thermostat");

        // 5. Nintendo Switch OLED 64GB — with Games vs plain White
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo Switch OLED 64GB Excellent Condition with Games",
                "The Nintendo Switch OLED 64GB is a handheld gaming system from Nintendo, released in 2021. This sleek white console features a 7-inch OLED display.",
                "Nintendo Switch OLED 64GB White",
                "Immerse yourself in gaming excellence with the Nintendo Switch OLED 64GB, a sleek white handheld system that offers a vibrant OLED screen."),
            "same", "Nintendo Switch OLED");

        // 6. Canon EF 50mm f/1.8 STM — both same lens
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Canon Ef 50mm F1.8 STM Lens",
                "A fantastic lens for portraits. The EF 50mm f/1.8 STM.",
                "Used Canon EF 50mm F1.8 STM Lens",
                "Canon EF 50mm f1.8 STM Lens is a great addition to any kit bag as it is extremely lightweight and compact."),
            "same", "Canon RF 50mm Lens");

        // 7. KitchenAid Artisan — Empire Red vs Onyx Black (appliance color)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "KitchenAid Artisan 4.8L Stand Mixer Empire Red 5KSM150PSBER",
                "Striking red makes a perfect accent colour. KitchenAid's Artisan Stand Mixer.",
                "KitchenAid Artisan Series 5 Quart Tilt-Head Stand Mixer - Onyx Black KSM150PSOB",
                "Artisan Series 5 Quart Tilt-Head Stand Mixer. This durable tilt-head stand mixer was built to last, and features 10 speeds."),
            "same", "KitchenAid Stand Mixer");

        // 8. PS5 Digital Edition 825GB — both brand new
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Playstation 5 Digital Edition 825 GB - White - Brand New",
                "The Playstation 5 Digital Edition with 825 GB storage capacity in White is a brand new home console from Sony.",
                "Sony PlayStation 5 Digital Edition 825GB Console with Controller",
                "The Sony PlayStation 5 Digital Edition 825GB Console with Controller is a sleek and powerful home console from Sony."),
            "same", "PlayStation 5 Console");

        // 9. PS5 825GB Disc — working boxed vs custom black console only
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "SONY PlayStation 5 PS5 825GB Disk Edition * 100% Fully working * FREE Next Day",
                "Sony PlayStation 5 Disk Edition 825GB Blu-Ray. Boxed. Fully working. Comes with official controller and all wires. Restored to factory settings.",
                "Sony PS5 825GB Blu-Ray Disc Edition Console Custom Black - Console Only tested",
                "Selling a Sony PS5 825GB Blu-Ray Disc Edition console with custom hand-painted black side panels. This listing is for the console only plus power supply - no controller, no box."),
            "same", "PlayStation 5 Console");

        // 10. Rolex Submariner 16610T vs 16610 — same ref essentially
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "2003 ROLEX MENS SUBMARINER DATE 16610T NO HOLES STAINLESS STEEL BLACK DIAL WATCH",
                "This is an authentic Rolex 16610T Submariner Date no holes 40mm black dial watch with stainless steel oyster bracelet.",
                "ROLEX Submariner 16610 Oyster Date SS Black Dial Men's Watch MINT CONDITION",
                "ROLEX Submariner 16610 Oyster Date SS Black Dial Men's Watch MINT CONDITION."),
            "same", "Rolex Submariner");

        // 11. Generic WiFi Video Doorbell — both same generic product
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Smart WiFi Wireless Video Doorbell Security Ring Phone Camera Door Bell Intercom",
                "Wireless WiFi Video Doorbell, featuring an HD camera, two-way audio intercom, motion detection, and night vision. Works with Tuya Smart App.",
                "2.4G WiFi Wireless Smart Video Doorbell Security Camera Ring Intercom Tuya App",
                "HD real-time video/two-way audio intercom. 2.4GHZ Wi-Fi wireless smart doorbell."),
            "same", "Ring Video Doorbell");

        // 12. Nest Learning Thermostat 3rd Gen — both with stand
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Google Nest Learning Thermostat 3rd Generation Stainless Steel with white stand",
                "Google Nest Learning Thermostat in Stainless Steel along with white stand. All items are used but in very good condition.",
                "Google Nest Learning Thermostat 3rd GENERATION with Stand",
                "Google Nest Thermostat (3rd Generation) with Stand ONLY!! (LINK NOT INCLUDED). Removed from a previous system after a few months."),
            "same", "Nest Thermostat");

        // 13. Ninja Foodi MAX Dual Zone 9.5L — both same air fryer
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "NINJA Foodi MAX Dual Zone 9.5L Air Fryer INCLUDES Silicone Tongs",
                "Brand new and fully warranted by Ninja guarantee. Save up to 65% on your oven energy bill.",
                "Ninja MAX Dual Zone Digital Air Fryer, 2 Drawers, 9.5L, 6-In-1, Uses No Oil, Air",
                "2 Independent Cooking Zones. Cook two different foods, two different ways, at the same time. 6 Cooking Functions."),
            "same", "Ninja Foodi Air Fryer");

        // 14. iPhone 15 Pro Max 256GB Unlocked — both same phone
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "iPhone 15 Pro Max 256GB Unlocked",
                "The iPhone 15 Pro Max 256GB Unlocked is a high-end mobile phone with large 256GB storage capacity.",
                "Apple iPhone 15 Pro Max 256GB Black Titanium, Unlocked",
                "The Apple iPhone 15 Pro Max 256GB in Black Titanium is a cutting-edge mobile phone. Unlocked status allows freedom to choose preferred network."),
            "same", "iPhone 15 Pro Max");

        // 15. iPhone 15 Pro Max 256GB — Blue Titanium Good vs Black Titanium Good
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple iPhone 15 Pro Max - 256GB Blue Titanium Unlocked - GOOD Condition WARRANTY",
                "Apple iPhone 15 Pro Max - 256GB Blue Titanium Unlocked to all Networks - Good Condition.",
                "Apple iPhone 15 Pro Max 256GB Black Titanium, Unlocked Good Condition",
                "The phone has been tested and passed 100%. All parts are original. 2 visible scratches on the screen. The body condition is 8/10. Unlocked to all networks."),
            "same", "iPhone 15 Pro Max");

        // === NOT COMPARABLE (ONNX=different) — 15 pairs ===

        // 16. Shark full vacuum vs wand tube accessory
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Shark Cordless Stick Vacuum Auto Empty System [BU3521UKTSB] - Refurbished",
                "Meet the cordless vacuum that empties itself, so you don't have to.",
                "Red/Silver Wand Tube Only for Shark ZU561QR Navigator Lift-Away Vacuum Cleaner",
                "Red/Silver Wand Tube Only for Shark ZU561QR Navigator Lift-Away Vacuum Cleaner."),
            "different", "Shark Navigator Vacuum");

        // 17. Switch OLED boxed vs Switch OLED + Pro controller bundle
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo Switch OLED - 64GB - White - Boxed Complete - Excellent Condition",
                "Nintendo Switch OLED in White - Excellent Condition. Boxed and complete with all accessories. Dock, Joycons, Straps and Gamepad, HDMI.",
                "Nintendo Switch OLED 64GB + 16GB White Pro controller case console not much use!",
                "Nintendo Switch OLED 64GB + 16GB Micro SD card White and Black Pro controller Xenoblade Chronicles 2. Case / bag."),
            "different", "Nintendo Switch OLED");

        // 18. DeWalt Impact Driver vs DeWalt Right Angle Drill
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dewalt DCF887N 18V XR G2 Brushless 3 Speed Impact Driver Precision Drive - Bare",
                "DeWalt DCF887N 18V XR G2 Brushless 3 Speed Impact Driver.",
                "Dewalt DCD740D2 18v XR 2 Speed Right Angle Drill Li-Ion + 2x2.0ah, Charger + Bag",
                "Dewalt DCD740D2 18v XR 2 Speed Right Angle Drill."),
            "different", "DeWalt Cordless Drill");

        // 19. Samsung 55" QN90F vs Samsung 75" QN95D — different size and model
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "SAMSUNG 55\" Neo QLED 4K Mini LED Vision AI Smart TV - QE55QN90F - REFURB-B",
                "SAMSUNG 55\" Neo QLED 4K Mini LED Vision AI Smart TV.",
                "SAMSUNG QE75QN95DATXXU 75\" Smart 4K Ultra HDR Neo QLED TV with Alexa",
                "SAMSUNG QE75QN95DATXXU 75\" Smart 4K Ultra HDR Neo QLED TV."),
            "different", "Samsung QN90C QLED TV");

        // 20. Milwaukee Fuel Gen4 kit vs Milwaukee One Key FUEL kit
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Milwaukee M18FPP2A3-502X Fuel Gen4 Combi Drill and Impact Driver Kit 2 x 5.0Ah",
                "Milwaukee M18FPP2A3-502X Fuel Gen4 Combi Drill & Impact Driver Kit - 2 x 5.0Ah.",
                "Milwaukee M18ONEPP2A3-502X M18 One Key FUEL Power Pack",
                "Kit includes: M18ONEPD3 ONE KEY combi drill, M18ONEID3 ONE KEY impact driver, 2x M18B5 5.0Ah batteries, rapid charger, Dynacase."),
            "different", "Milwaukee M18 Impact Driver");

        // 21. LEGO Kylo Ren 75256 vs 75406 — different sets
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "LEGO Star Wars Kylo Ren's Shuttle 75256 Building Kit Complete Set",
                "Lego 75256 Kylo Ren's Shuttle. Complete with wicked bricks acrylic display stand. Built once displayed out of direct sunlight.",
                "Lego Star Wars Kylo REN's Command Shuttle (75406) 18+ New & Sealed",
                "The Lego Star Wars Kylo REN's Command Shuttle (75406) is a highly sought-after set aimed at fans aged 18 and above."),
            "different", "LEGO Star Wars Set");

        // 22. NB 990v6 Women's 6 vs Men's 9 Grey Day — different size/colorway
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "New Balance 990v6 Made in USA - Women's 6 - Grey - Brand New",
                "Brand new pair of New Balance 990v6 (Made in USA) in the classic Grey (W990GL6) colorway. Size: Women's 6. Width: B (Medium).",
                "New Balance 990v6 Grey Day U990TC6 Size 9",
                "New Balance 990v6 Grey Day U990TC6 Size 9. Great condition, 100% Authentic with replacement box."),
            "different", "New Balance 990v6");

        // 23. Lenovo X1 Carbon Gen 6 i5 8GB vs Gen 6 i7 16GB
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "LENOVO THINKPAD X1 CARBON, GEN 6, CORE i5 8TH GEN, 8GB RAM, 256 SSD, 14\" FHD",
                "Lenovo ThinkPad X1 Carbon, Gen 6. Intel Core i5 8th Gen processor, 8GB RAM, 256GB SSD, 14-inch FHD screen.",
                "Lenovo Thinkpad X1 Carbon Intel Core i7 6th gen 6600/6500 16GB 512Gb Touchscreen",
                "Lenovo ThinkPad X1 Carbon 14\" Laptop, Intel Core i7 (6th Gen) 16GB DDR3 ram, 512GB m2 SSD, touchscreen."),
            "different", "Lenovo ThinkPad X1 Carbon");

        // 24. Pandora bracelet with 10 charms vs Pandora heart clasp bracelet
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Authentic Pandora 925 ALE Silver 18cm Snake Chain Charm Bracelet 10 Charms",
                "Authentic Pandora 925 ALE Silver 18cm Snake Chain Charm Bracelet 9 Pandora Bead Charms & A Silver Locket Charm. Good Pre-Owned Condition.",
                "GENUINE PANDORA HEART CLASP BRACELET (19.5CMS)",
                "GENUINE PANDORA HEART CLASP BRACELET (19.5CMS). VERY GOOD CONDITION. NO BOX."),
            "different", "Pandora Charm Bracelet");

        // 25. Levi's 501 W29 L28 vs W32 L32 — different sizes
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Levi's 501 Light Blue Straight Leg Jeans W29 L28 Button Fly Vintage Style Red Ta",
                "Classic Levi's 501 jeans in a light blue wash, men's W29 L28. Straight-leg fit with button fly. Very good pre-owned condition.",
                "Levi's 501 Mens Jeans W32 L32 Blue Button Fly Straight Leg Vintage Fade",
                "Iconic Levi's 501 Men's Jeans in size W32 L32, crafted from durable cotton denim in a blue vintage wash."),
            "different", "Vintage Levis 501 Jeans");

        // 26. LEGO Technic Monster Mutt vs McLaren — different sets
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "LEGO Technic Monster Jam 42150 Monster Mutt Dalmatian Unopened Box Retired Set",
                "",
                "LEGO Technic Neom McLaren Extreme E Race Car 42166 Unopened Box Retired Set",
                ""),
            "different", "LEGO Technic Set");

        // 27. iPhone 12 Pro Max 128GB vs 512GB — different storage
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple iPhone 12 Pro Max with New Battery - 128GB - Unlocked - Graphite - Good",
                "Apple iPhone 12 Pro Max 128GB Unlocked Graphite. Good condition with signs of wear including scratches, visible scuffs.",
                "Apple iPhone 12 Pro Max 512GB Graphite (EE)",
                "Apple iPhone 12 Pro Max smart phone good condition 6.7 inch OLED display. 80% battery life."),
            "different", "iPhone 15 Pro Max");

        // 28. Ray-Ban Wayfarer Classic Polarized 50mm vs Ray-Ban Wayfarer Black
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Ray-Ban Sunglasses Wayfarer Classic Black Frame Dark Green Lens 50mm",
                "Ray-Ban Sunglasses Wayfarer Classic in black frame with green lens. Square style with polarized glass lenses. UV400 protection.",
                "Ray-Ban Wayfarer Sunglasses Black",
                "Ray-Ban Wayfarer Sunglasses in Black. Made in Italy. Iconic Ray-Ban branding, sleek black acetate frame, glass lenses with UV400 protection."),
            "different", "Ray-Ban Wayfarer Sunglasses");

        // 29. Yeezy 350 V2 White UK11.5 vs Desert Sage UK12 — different colorway and size
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "adidas Yeezy Boost 350 V2 Men's White UK11.5",
                "The adidas Yeezy Boost 350 V2 Men's White UK11.5 is a stylish trainer. The white colourway with rubber outsole.",
                "adidas Yeezy Boost 350 V2 Desert Sage (FX9035) size UK12",
                "The adidas Yeezy Boost 350 V2 Desert Sage (FX9035). Desert Sage colour, synthetic upper material."),
            "different", "Adidas Yeezy Boost 350");

        // 30. Callaway Paradym X vs Paradym AI Smoke Max — different model
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Callaway Paradym X Driver / 12 Degree / HZRDUS Regular Flex",
                "Callaway Paradym X Driver, 12 Degree, HZRDUS Regular Flex shaft.",
                "Callaway Paradym AI Smoke Max Driver / 12 Degree / Denali 60 Stiff Flex",
                "Callaway Paradym AI Smoke Max Driver, 12 Degree, Denali 60 Stiff Flex shaft."),
            "different", "Callaway Paradym Driver");
    }

    // -----------------------------------------------------------------------
    // DIAGNOSTIC BATCH 3 — 30 new random DB pairs
    // Run with: dotnet test --filter "Should_classify_real_db_pairs_batch3"
    // -----------------------------------------------------------------------

    [Test]
    [Explicit("Manual diagnostic — not for CI")]
    public async Task Should_classify_real_db_pairs_batch3()
    {
        var pairs = RealDbPairsBatch3().ToList();
        var requests = pairs.Select(p => p.Arguments[0] as ClassifyPairRequest).ToList();

        var results = await _classifier.Classify(requests!);

        for (var i = 0; i < pairs.Count; i++)
        {
            var onnxLabel = (string)pairs[i].Arguments[1]!;
            var category = (string)pairs[i].Arguments[2]!;
            var llmLabel = results[i].IsComparable ? "same" : "different";
            var marker = llmLabel == onnxLabel ? " OK" : "!!!";
            var confidence = results[i].Confidence;

            TestContext.WriteLine($"[{marker}] ONNX={onnxLabel,-9} LLM={llmLabel,-9} conf={confidence:F1} | {category}");
            TestContext.WriteLine($"      A: {requests[i]!.TitleA}");
            TestContext.WriteLine($"      B: {requests[i]!.TitleB}");
            if (results[i].Reason is { } reason)
            {
                TestContext.WriteLine($"      Reason: {reason}");
            }
            TestContext.WriteLine();
        }

        var agree = results.Select((r, i) =>
        {
            var onnx = (string)pairs[i].Arguments[1]!;
            var llm = r.IsComparable ? "same" : "different";
            return llm == onnx;
        }).Count(x => x);

        TestContext.WriteLine($"Agreement: {agree}/{results.Count} ({agree * 100 / results.Count}%)");
    }

    private static IEnumerable<TestCaseData> RealDbPairsBatch3()
    {
        // === COMPARABLE (ONNX=same) — 15 pairs ===

        // 1. Sonos One SL — White good condition vs Excellent condition
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Sonos ONE SL White - 003",
                "Sonos One SL wireless speaker. Good condition with only a few minor scratches and no dents. Fully functional and comes with a power cable. Same day shipping.",
                "Sonos One SL Wireless Speaker - Excellent Condition",
                "Excellent condition. It has only ever been moved/touched when unboxing/boxing. Comes in original packaging with all cables that came with it."),
            "same", "Sonos One SL Speaker");

        // 2. Whoop 4.0 Band Replacement — both generic replacement straps
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Soft for Whoop 4.0 Band Sports Men Adjustable Replacement Strap Breathable",
                "Brand new and high quality. Band Replacement for Whoop 4.0. Strap Size: 232*25mm. Compatible Devices: for Whoop 4.0.",
                "Replacement Strap Adjustable Accessories Bracelet Whoop 4.0/3.0 Band Nylon Sport",
                "Perfect match with Whoop 3.0 and 4.0: Enhance your heart rate monitoring experience with our armband changes. Ultra-light, soft and skin-friendly materials."),
            "same", "Whoop 4.0 Band");

        // 3. Yeezy 350 UK 10 black vs Hyperspace UK 10 — different colorway (fashion)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "adidas Yeezy Boost YZY 350 Men's UK Size 10",
                "The adidas Yeezy Boost YZY 350 Men's UK Size 10 is a stylish low-top trainer. Black sneaker from the adidas Yeezy product line with rubber outsole.",
                "Adidas Yeezy Boost 350 V2 \"Hyperspace\" Men's Size 10 Trainers (EG7491)",
                "Adidas Yeezy Boost 350 V2 \"Hyperspace\" - Asia-exclusive release in men's UK 10. Light grey/green Primeknit upper. Clean pre-owned condition. No box - shoes only."),
            "same", "Adidas Yeezy Boost 350");

        // 4. Brompton M6L Black Edition vs M6L Electric — different product (electric vs manual)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Brompton M6l Black Edition 6 Speed 2020",
                "Brompton electric Black Edition 6 Speed 2020. Complete with Brompton charger and the practical removable battery in a Brompton bag, 250w motor.",
                "Brompton M6L Electric Folding Bike 2021",
                "Condition: Excellent. Pre-Owned Used. Brand: Brompton. Model: M6L Electric. Year: 2021. Colour: Bolt Blue. Weight: 17.34 kg."),
            "same", "Brompton Folding Bike");

        // 5. Switch OLED REFURB-C vs Screen Only — different completeness
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "NINTENDO Switch OLED - Neon Red and Blue - REFURB-C",
                "NINTENDO Switch OLED - Neon Red and Blue - REFURB-C",
                "Nintendo Switch OLED Edition 64GB Games Console - Screen Only (U)",
                "Nintendo Switch OLED Edition 64GB Games Console - Screen Only (U). Pre-owned."),
            "same", "Nintendo Switch OLED");

        // 6. WiFi Video Doorbell — both generic smart doorbells
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Wireless Smart Video Doorbell WiFi Security Camera Bell Phone Door Ring intercom",
                "High-quality ABS material. Ultra-high-definition camera lens and a 125 degree wide-angle lens. Built-in two-way microphone.",
                "HD Video Doorbell Camera Smart WiFi Wireless Ring Bell with Receiver UK",
                "Smart Video Doorbell Camera Wireless Indoor Outdoor Surveillance AI-Powered Human Detection. Secure Cloud Storage."),
            "same", "WiFi Video Doorbell");

        // 7. Switch OLED 64GB — both same console
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nintendo Switch OLED Model HEG-001 Handheld Console - 64GB",
                "The Nintendo Switch OLED Model HEG-001 offers a sleek design with upgraded OLED display, delivering stunning visuals in 720p and 1080p resolution. 64GB storage.",
                "Nintendo Switch OLED Model HEG-001 Handheld Console - 64GB - White",
                "Discover endless gaming fun with the Nintendo Switch OLED Model HEG-001 Handheld Console. 64GB storage capacity."),
            "same", "Nintendo Switch OLED");

        // 8. Birkenstock Arizona Tan sz 40 NEW vs Black sz 40 used — different color+material (fashion)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Birkenstock Tan Arizona Slide Sandals sz 40 Ladies 9 Mens 7 NEW Soft Footbed",
                "Birkenstock Tan Arizona Slide Sandals in size 40 (EU). Adjustable strap with buckle, leather outsole, suede insole. Beige color.",
                "Birkenstock Arizona Sandals Womens Size 9 Black Buckle Two Strap Cork Shoes",
                "Birkenstock Arizona two-strap slide sandals. Color: Black with black buckles. Size: EU 40 - US Womens 7 / Womens 9. Made in Germany."),
            "same", "Birkenstock Arizona Sandals");

        // 9. Bose QuietComfort Ultra Earbuds Black vs White — electronics color
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Bose QuietComfort Ultra 2nd Gen Earbuds Black",
                "Brand new sealed. Genuine product. Bose QuietComfort Ultra 2nd Gen Earbuds in Black. Bluetooth wireless technology.",
                "Bose QuietComfort Ultra Bluetooth Earbuds, Wireless Noise Cancelling",
                "BRAND NEW SEALED BOSE QUIET COMFORT ULTRA EARBUDS - NOISE CANCELLING - WHITE. 2 Year Manufacturer Warranty. FREE NEXT DAY DELIVERY."),
            "same", "Bose QC Ultra Earbuds");

        // 10. Dewalt 18V SDS Hammer Drill — both generic compatible tools
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "For Dewalt 18V 20V Cordless Brushless Hammer SDS Plus Rotary Drill Tools",
                "For Dewalt 18V 20V battery. Brushless. Battery Not Included. Variable speed trigger allows the user to adjust the speed.",
                "Brushless Cordless Drill SDS Rotary Electric Impact Hammer For Dewalt 18V 20V",
                "Brushless Cordless Drill SDS Rotary Electric Impact Hammer For Dewalt 18V 20V."),
            "same", "Dewalt SDS Hammer Drill");

        // 11. MX Master mouse travel bag — both generic EVA cases
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Protective EVA Travel Storage Bag for Wireless Mouse MX Master 2S 3 G502 Case UK",
                "Shockproof and waterproof EVA travel bag designed specifically for the MX Master 2S 3 G502 wireless mouse. 6 x 15 x 11 cm.",
                "For MX Master 3/3S Gaming Mouse Portable Storage Bag Shockproof Box5537",
                "Specially designed for MX Master 3/2S Mice Gaming Mouse. Made of high quality EVA and Oxford cloth material, shockproof, waterproof."),
            "same", "MX Master Mouse Case");

        // 12. Dr Martens 1460 8 Eye vs 7 Eye Steel Toe — different product (eye count, steel toe)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Vintage Dr. Martens 1460 Made in England Black Leather Boots UK 8 | 8 Eye",
                "Vintage Dr. Martens 1460 Made in England Black Leather Boots UK 8. 8 Eye. Excellent condition for the age. All stitching is solid.",
                "Dr Martens Vintage 90s Made in England Black Steel Toe 7 eye size UK 8 (434)",
                "Dr Martens Vintage 90s Made in England Black Steel Toe boots Size UK 8. 7 eyelets. Steel toe. Genuine Leather. Excellent vintage condition."),
            "same", "Dr Martens 1460 Boots");

        // 13. Dyson Airwrap I.D. Multi-Styler — both same product
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dyson Airwrap I.D. Multi-Styler",
                "Dyson Airwrap I.D. Multi-Styler bought from Boots in December 2024. Perfect working condition with all parts and accessories included. Aqua and orange in colour.",
                "Dyson Airwrap I.D. Multi-Styler",
                "New Dyson Airwrap i.d. multi-styler and dryer - Straight+Wavy (Vinca Blue/Topaz). With Bluetooth. Bought December 2024. RRP 479.99, bought direct from Dyson."),
            "same", "Dyson Airwrap Multi-Styler");

        // 14. Dyson Airwrap Complete Long Blue/Pink vs Vinca Blue — same product, color trivial
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Dyson Airwrap Multi-Styler Long Complete Edition Blue And Pink",
                "Dyson Airwrap Multi-Styler Long Complete Edition in Blue and Pink. Ceramic heating plate and three temperature settings up to 150C. Comes with attachments shown.",
                "Dyson Airwrap Complete Long Special Edition Hair Multi-Styler Set - Vinca Blue",
                "Dyson Airwrap Complete Long Special Edition in Vinca Blue. Brand new, never used; comes complete with all attachments, styling comb and storage box. Maximum temperature 150C."),
            "same", "Dyson Airwrap Complete Long");

        // 15. Ninja AF100UK 3.8L Air Fryer — both same model
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Ninja AF100UK 3.8L 1550W Air Fryer - see photos",
                "Ninja AF100UK 3.8L 1550W Air Fryer. Maximum timer setting of 60 minutes. Black steel design. 3.8L oil capacity. Adjustable temperature range of 100C to 220C.",
                "Ninja AF100UK 3.8L 1550W Air Fryer",
                "Ninja AF100UK 3.8L 1550W Air Fryer. Large 3.8L capacity and 1550W power. Durable steel, stylish black colour."),
            "same", "Ninja AF100UK Air Fryer");

        // === NOT COMPARABLE (ONNX=different) — 15 pairs ===

        // 16. Bugaboo Fox Cub vs Bugaboo Fox 5 — different model
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Brand New Bugaboo Fox Cub 2-in-1 Pram - Complete Set - Never Used.",
                "Brand New Bugaboo Fox Cub 2-in-1 Pram and Pushchair - Black. Never used, never assembled. Complete set.",
                "Bugaboo fox 5 Desert Taupe 2in1 Pram (Newest model)",
                "We bought this pram brand new only one year ago. Very good condition and kept very clean. Durable pram with great suspension."),
            "different", "Bugaboo Pram");

        // 17. Keychron K10 vs Keychron K1 — different model
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Keychron K10 Wireless Mechanical Keyboard",
                "Keychron K10 Wireless Mechanical Keyboard. K10 Rgb Backlight Aluminium Red Switch. QWERTY layout, wireless connectivity.",
                "Keychron K1 QMK Ultra-Slim Wireless Bluetooth/Wired Mechanical Keyboard",
                "Keychron K1 QMK Ultra-Slim Wireless Bluetooth/Wired Mechanical Keyboard, TKL Layout, RGB LED Backlight, Hot-swappable Low-Profile Brown Switch for Mac/Win/Linux - UK Layout."),
            "different", "Keychron Keyboard");

        // 18. Corsair Vengeance DDR5 6000MHz vs 6400MHz — different spec
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "CORSAIR VENGEANCE RGB DDR5 RAM 32GB (2x16GB) 6000MHz CL30 CMH32GX5M2B6000Z30K",
                "Pulled from my current setup as I've upgraded to a 64gb kit. In excellent working condition.",
                "CORSAIR VENGEANCE RGB DDR5 RAM 32GB (2x16GB) 6400 MHz CMH32GX5M2B6400C36W",
                "Using these sticks at the moment but wouldn't mind selling them. Perfect working order, purchased 30/07/2024."),
            "different", "Corsair Vengeance DDR5 RAM");

        // 19. iPhone 13 Pro Max 128GB vs iPhone 15 Pro Max 256GB — different model AND storage
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Apple iPhone 13 Pro Max 128 gb Space Black/Graphite",
                "Apple iPhone 13 Pro Max, Hexa Core processor, Apple A15 Bionic chipset, 6.7-inch screen. 128GB storage, 6GB RAM. Factory unlocked.",
                "Apple iPhone 15 Pro Max - 256GB - Black Titanium (Unlocked)",
                "Apple iPhone 15 Pro Max in Black Titanium. 6.7-inch screen, Hexa Core, Apple A17 Pro. Water and dust resistance, facial recognition."),
            "different", "iPhone Pro Max");

        // 20. PlayStation Portal PS5 white vs Midnight Black — different color (electronics)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "PlayStation Portal PS5",
                "Selling this as never really used. Bought for my son Christmas 2025. Turned on once. As new condition. White, 1TB storage.",
                "PS Portal",
                "Barely used Sony PlayStation Portal in Midnight Black, excellent condition. Complete with original box. No charger included."),
            "different", "PlayStation Portal");

        // 21. Brompton M6R 6-speed vs Brompton 3-speed — different spec (gear count)
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Brompton M6R Folding Fold-up Foldable Bike",
                "A lovely red 2011 Brompton M6R complete with front luggage block, rear triangle retaining clip. 6-speed.",
                "Brompton Folding Bicycle. Red (3-Speed Sturmey Archer Hub Gears.",
                "Brompton folding bike in red, equipped with reliable Sturmey Archer 3-speed gear system. Purchased in the early 2000s. Very lightly used."),
            "different", "Brompton Folding Bike");

        // 22. Yeezy 350 V2 Beluga Reflective UK 8.5 vs US 8 — different size
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Adidas Yeezy Boost 350 V2 Beluga Reflective UK 8.5 - Brand New Deadstock",
                "100% genuine Adidas Yeezy Boost 350 V2 Beluga Reflective UK 8.5. Brand new deadstock. Never been worn.",
                "BRAND NEW adidas Yeezy Boost 350 V2 Beluga Reflective Men's Size US 8",
                "Original box included but damaged. Shoes brand new never worn. Beluga Reflective color, low top, size US 8. Released December 2021."),
            "different", "Adidas Yeezy Boost 350 V2");

        // 23. Yamaha YPP 200 37-key vs Yamaha P-140 88-key — different model entirely
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Yamaha YPP 200 Electric Piano with Weighted Keys",
                "Yamaha YPP 200 Electric Piano features 37 weighted keys. Comes in black and includes a stand. Digital home piano.",
                "Yamaha Electric Piano P-140 2020's - Black",
                "Yamaha Electric Piano P-140 in black. Digital home piano with 88 keys."),
            "different", "Yamaha Electric Piano");

        // 24. Nike Dunk Low Retro Panda SE Viotech sz 9 vs Dark Marina Blue sz 9.5 — different colorway + size
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Nike Dunk Low Retro Panda SE Viotech/Pale Ivory/Veneer Size 9 IB2990-500 BNIB",
                "Nike Dunk Low Retro SE Viotech/Pale Ivory/Veneer BNIB Size: 9 (UK). 40 years strong, limited-edition colour-blocking.",
                "Nike Dunk Low Blue UK 9.5 Dark Marina Sports Casual Retro Leather White Swoosh",
                "Nike Dunk Low Dark Marina Blue UK 9.5. Sold as used, very good cosmetic condition. Minor marks and scuffs."),
            "different", "Nike Dunk Low");

        // 25. Synology DS415+ 8TB vs DS414J 4TB — different model and storage
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Synology DS415+ 4-Bay NAS | 8TB (2x4TB WD Red) | 2GB RAM | Fully Working",
                "Synology DS415+ 4-bay NAS with 2x4TB WD Red NAS drives and all 4 original drive trays. Used, great overall condition, fully functional.",
                "Synology DiskStation DS414J 4-Bay NAS Network Attached Storage & 4TB of storage",
                "Great sturdy NAS device. Comes with 2x2TB Hitachi hard disks. Good cosmetic condition."),
            "different", "Synology NAS");

        // 26. North Face Puffer 700 Polyester vs Down Nylon — different fill material
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "The North Face Black Puffer Jacket Size L Other Sizes Available",
                "North Face Black Puffer Jacket size L. Softshell polyester outer shell with polyester lining. 700 insulation weight. Stand-up collar and zip closure.",
                "North Face Puffer Jacket Men",
                "North Face Men's Puffer Jacket size L. Durable nylon outer shell and insulated with warm down. Mid-length, softshell fabric and polyester lining."),
            "different", "North Face Puffer Jacket");

        // 27. Banksy Nola Girl framed print vs canvas poster — different format
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Banksy Nola Girl with Umbrella White FRAMED ART PRINT Picture Poster Artwork",
                "High Quality Framed Art Prints. Printed and framed to order. High resolution, Ultrachrome Inks. Professionally framed.",
                "Banksy Style Umbrella Girl Canvas Picture Poster Print Wall Art",
                "Handmade in the UK. Premium Quality: Image printed on quality canvas art paper with white edge border. Fade-resistant inks."),
            "different", "Banksy Art Print");

        // 28. HP Color LaserJet Pro MFP 3302fdw vs HP LaserJet Pro M255dw — different model
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "HP Color LaserJet Pro MFP 3302fdw A4 Colour Laser Printer (6 Month HP Warranty)",
                "61 pages printed, ink level almost full. HP Color LaserJet Pro MFP 3302fdw all-in-one printer. Colour printing, scanning, copying, and faxing.",
                "HP LaserJet Pro M255dw Laserjet All-In-One Printer -NEW",
                "NEW HP LASERJET PRO M255DW. Black print speed of 22 pages per minute. Printing, scanning, and copying."),
            "different", "HP LaserJet Printer");

        // 29. Callaway Paradym Ai Smoke Max Right Hand vs Left Hand — different dexterity
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Callaway Paradym Ai Smoke Max Driver / 10.5 Degree / Regular Flex Kai'li Red 60",
                "Dexterity: Right-Handed. Model: Paradym Ai Smoke Max. Driver. Loft: 10.5. Flex: Regular. Shaft: Kai'li Red 60. Graphite. 45 inches.",
                "Left Hand Callaway Paradym Ai Smoke Max Driver / 10.5 Degree / Regular Flex 40",
                "Dexterity: Left-Handed. Model: Paradym Ai Smoke Max. Driver. Loft: 10.5. Flex: Regular. Shaft: Cypher 2.0 40. Graphite. 45.5 inches."),
            "different", "Callaway Paradym Driver");

        // 30. Ubiquiti UAP-AC-M-PRO vs UAP-PRO — different model
        yield return new TestCaseData(
            new ClassifyPairRequest(
                "Ubiquiti UniFi AC Mesh Pro - White (UAP-AC-M-PRO) Wireless Access Point",
                "Ubiquiti UniFi AC Mesh Pro (UAP-AC-M-PRO). Maximum LAN data rate 1000 Mbps, wireless data rate 450 Mbps. Dual-band extender.",
                "Ubiquity Unifi AP-PRO (UAP-PRO) Wireless Access Point, Power Supply, Brackets",
                "Ubiquity Unifi AP-PRO (UAP-PRO). Maximum LAN data rate 1000 Mbps. Dual-band access point, 2.4 GHz and 5 GHz."),
            "different", "Ubiquiti UniFi Access Point");
    }
}
