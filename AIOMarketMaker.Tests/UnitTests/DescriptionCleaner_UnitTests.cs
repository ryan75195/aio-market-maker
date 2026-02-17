using AIOMarketMaker.Core.Utils;

namespace AIOMarketMaker.Tests.Unit
{
    [TestFixture]
    [Category("Unit")]
    public class DescriptionCleaner_UnitTests
    {
        private static string DescriptionsDataDir =>
            Path.GetFullPath(Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "../../../Data/Descriptions"));

        private static string LoadTestFile(string fileName) =>
            File.ReadAllText(Path.Combine(DescriptionsDataDir, fileName));

        // --- Null / empty ---

        [Test]
        public void Should_return_null_when_input_is_null()
        {
            var result = DescriptionCleaner.Clean(null);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Should_return_null_when_input_is_empty()
        {
            var result = DescriptionCleaner.Clean("");
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Should_return_null_when_input_is_whitespace()
        {
            var result = DescriptionCleaner.Clean("   \n\t  ");
            Assert.That(result, Is.Null);
        }

        // --- Tag removal (inline cases) ---

        [Test]
        public void Should_remove_style_tags_and_extract_text()
        {
            var html = "<style>.foo { color: red; }</style><p>Product description here</p>";
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Is.EqualTo("Product description here"));
        }

        [Test]
        public void Should_return_null_when_only_style_tags()
        {
            var html = "<style>.foo { color: red; } .bar { margin: 0; }</style>";
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Should_remove_script_tags()
        {
            var html = "<script>var x = 1;</script><p>Real text</p>";
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Is.EqualTo("Real text"));
        }

        [Test]
        public void Should_remove_nav_elements()
        {
            var html = "<nav><a href='/'>Home</a><a href='/shop'>Shop</a></nav><p>Product info</p>";
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Is.EqualTo("Product info"));
        }

        [Test]
        public void Should_remove_header_and_footer()
        {
            var html = "<header><h1>Store Name</h1></header><p>Product details</p><footer>Copyright 2026</footer>";
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Is.EqualTo("Product details"));
        }

        [Test]
        public void Should_remove_elements_with_nav_class()
        {
            var html = "<div class=\"store-nav\"><a>Menu</a></div><p>Actual content</p>";
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Is.EqualTo("Actual content"));
        }

        [Test]
        public void Should_remove_elements_with_menu_class()
        {
            var html = "<ul class=\"main-menu clearfix\"><li>Shop</li><li>Contact</li></ul><p>Item info</p>";
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Is.EqualTo("Item info"));
        }

        [Test]
        public void Should_remove_elements_with_sidebar_class()
        {
            var html = "<div class=\"sidebar-product\"><span>Related Items</span></div><p>Main product</p>";
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Is.EqualTo("Main product"));
        }

        // --- Pass-through ---

        [Test]
        public void Should_pass_through_clean_html()
        {
            var html = "<p>PS5 Pro, barely used</p>";
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Is.EqualTo("PS5 Pro, barely used"));
        }

        [Test]
        public void Should_preserve_text_from_divs_and_tables()
        {
            var html = "<div>Product Details</div><table><tr><td>Weight</td><td>2kg</td></tr></table>";
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Does.Contain("Product Details"));
            Assert.That(result, Does.Contain("Weight"));
            Assert.That(result, Does.Contain("2kg"));
        }

        // --- Whitespace normalization ---

        [Test]
        public void Should_replace_nbsp_with_space()
        {
            var html = "<p>Good\u00A0condition</p>";
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Is.EqualTo("Good condition"));
        }

        [Test]
        public void Should_collapse_whitespace()
        {
            var html = "<p>A   \n\n  B</p>";
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Is.EqualTo("A B"));
        }

        // --- Real HTML file tests ---

        [Test]
        public void Should_clean_real_css_contaminated_html()
        {
            var html = LoadTestFile("CssContaminated.html");
            var result = DescriptionCleaner.Clean(html);

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("TaylorMade Stealth 2 Driver"));
            Assert.That(result, Does.Contain("HEAD ONLY"));
            Assert.That(result, Does.Not.Contain("font-size"));
            Assert.That(result, Does.Not.Contain("margin"));
            Assert.That(result, Does.Not.Contain("border-radius"));
        }

        [Test]
        public void Should_return_null_for_css_only_html()
        {
            var html = LoadTestFile("CssOnly.html");
            var result = DescriptionCleaner.Clean(html);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Should_clean_real_storefront_boilerplate_html()
        {
            var html = LoadTestFile("StorefrontBoilerplate.html");
            var result = DescriptionCleaner.Clean(html);

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("Canon EF 50mm"));
            Assert.That(result, Does.Not.Contain("font-size"));
            Assert.That(result, Does.Not.Contain("margin-right"));
        }

        [Test]
        public void Should_pass_through_clean_html_file()
        {
            var html = LoadTestFile("CleanDescription.html");
            var result = DescriptionCleaner.Clean(html);

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain("Poké Ball"));
            Assert.That(result, Does.Contain("Pokémon TCG"));
            Assert.That(result, Does.Contain("3 Pokémon TCG booster packs"));
        }
    }
}
