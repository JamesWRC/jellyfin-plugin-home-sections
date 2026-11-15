using Jellyfin.Plugin.HomeScreenSections.Attributes;
using Jellyfin.Plugin.HomeScreenSections.Helpers;
using Jellyfin.Plugin.HomeScreenSections.Model;
using Xunit;

namespace Jellyfin.Plugin.HomeScreenSections.Tests
{
    /// <summary>
    /// The splice of loadSections.js into jellyfin-web's chunk. Every minified name it takes from that chunk's scope is a hook
    /// of a per-version table; contract/web-hooks.json (plugin-tests contract tier) checks the table against the real bundle.
    /// </summary>
    public sealed class TransformationPatchesTests
    {
        private static string Patch()
        {
            // the chunk only has to carry ",loadSections:" and a `var x=` before it
            PatchRequestPayload payload = new() { Contents = "var q=r(1);var n={a:1,loadSections:function(){return 1},destroySections:function(){}}" };
            return TransformationPatches.LoadSections(payload);
        }

        [Fact]
        public void Every_placeholder_is_replaced_for_the_built_version()
        {
            string patched = Patch();
            Assert.DoesNotContain("{{", patched);
        }

        [Fact]
        public void Twelve_x_uses_the_12_x_names_for_the_image_helper_and_the_dashboard_link()
        {
            string version = JellyfinVersionAttribute.GetVersion() ?? "";
            Assert.StartsWith("12.", version);
            string patched = Patch();

            // 12.x bundles: getLibraryIcon lives in A (module 32203, export Ay), Dashboard.navigate in c (module 89100, export Ay).
            // b.Ay is escape-html and l is the ServerConnections module (no default export) in every 12.x bundle.
            Assert.Contains("var imageHelper = A.Ay;", patched);
            Assert.Contains("c.Ay.navigate(\"dashboard/libraries\")", patched);
            Assert.DoesNotContain("b.Ay", patched);
            Assert.DoesNotContain("l.default", patched);
        }
    }
}
