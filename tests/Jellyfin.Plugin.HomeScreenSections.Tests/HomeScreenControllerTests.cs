using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.HomeScreenSections.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using IHomeScreenManager = Jellyfin.Plugin.HomeScreenSections.Library.IHomeScreenManager;

namespace Jellyfin.Plugin.HomeScreenSections.Tests
{
    public sealed class HomeScreenControllerTests
    {
        private static int s_user;

        private static (HomeScreenController Controller, ListLogger<HomeScreenController> Log, string User) NewController()
        {
            ListLogger<HomeScreenController> log = new();
            // Meta and ClientLog use only the logger, the static plugin configuration and the caller's identity.
            HomeScreenController controller = new(Mock.Of<IHomeScreenManager>(), null!, null!, null!, null!, null!, null!, log);
            // A new name per test: the per-user rate limit is process-wide state.
            string user = "client-log-user-" + Interlocked.Increment(ref s_user);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, user) }, "test")) }
            };
            return (controller, log, user);
        }

        private static JsonElement Meta(Action<Configuration.PluginConfiguration> config)
        {
            TestPlugin.Configure(config);
            var (controller, _, _) = NewController();
            ObjectResult ok = Assert.IsType<OkObjectResult>(controller.GetUserMeta().Result);
            return JsonSerializer.SerializeToElement(ok.Value);
        }

        // ---- Meta ---------------------------------------------------------------------------------------------

        [Theory]
        [InlineData(-5, 200)]
        [InlineData(0, 200)]
        [InlineData(199, 200)]
        [InlineData(200, 200)]
        [InlineData(1200, 1200)]
        [InlineData(10000, 10000)]
        [InlineData(10001, 10000)]
        [InlineData(int.MaxValue, 10000)]
        public void Meta_clamps_the_reveal_timeout_to_200_through_10000_ms(int configured, int expected)
        {
            Assert.Equal(expected, Meta(c => c.RevealTimeoutMs = configured).GetProperty("RevealTimeoutMs").GetInt32());
        }

        [Theory]
        [InlineData("Enabled", "Enabled")]
        [InlineData("AllowUserOverride", "AllowUserOverride")]
        [InlineData("PaginationEnabled", "LazyLoadEnabled")]
        [InlineData("RevealSectionsTogether", "RevealSectionsTogether")]
        [InlineData("PreloadSectionImages", "PreloadSectionImages")]
        [InlineData("ShowBootSkeleton", "ShowBootSkeleton")]
        [InlineData("ClientDiagnostics", "ClientDiagnostics")]
        public void Meta_exposes_each_option_flag_from_its_own_setting(string exposedAs, string configProperty)
        {
            string[] flags = { "Enabled", "AllowUserOverride", "LazyLoadEnabled", "RevealSectionsTogether", "PreloadSectionImages", "ShowBootSkeleton", "ClientDiagnostics" };
            string[] exposed = { "Enabled", "AllowUserOverride", "PaginationEnabled", "RevealSectionsTogether", "PreloadSectionImages", "ShowBootSkeleton", "ClientDiagnostics" };

            JsonElement meta = Meta(c =>
            {
                foreach (string f in flags) typeof(Configuration.PluginConfiguration).GetProperty(f)!.SetValue(c, false);
                typeof(Configuration.PluginConfiguration).GetProperty(configProperty)!.SetValue(c, true);
            });

            foreach (string name in exposed)
            {
                Assert.Equal(name == exposedAs, meta.GetProperty(name).GetBoolean());
            }
        }

        [Fact]
        public void Meta_exposes_the_page_size()
        {
            Assert.Equal(7, Meta(c => c.NumSectionsPerPage = 7).GetProperty("NumResultsPerPage").GetInt32());
        }

        // ---- ClientLog ----------------------------------------------------------------------------------------

        private static HomeScreenController.ClientLogPayload Payload(params string[] events) =>
            new() { Build = "b1", Ua = "UA/1.0", Events = events };

        [Fact]
        public void ClientLog_does_nothing_and_logs_nothing_when_diagnostics_are_off()
        {
            TestPlugin.Configure(c => c.ClientDiagnostics = false);
            var (controller, log, _) = NewController();

            Assert.IsType<NoContentResult>(controller.ClientLog(Payload("hello")));
            Assert.Empty(log.Lines);
        }

        [Fact]
        public void ClientLog_ignores_a_post_without_events()
        {
            TestPlugin.Configure(c => c.ClientDiagnostics = true);
            var (controller, log, _) = NewController();

            Assert.IsType<NoContentResult>(controller.ClientLog(new HomeScreenController.ClientLogPayload { Build = "b" }));
            Assert.Empty(log.Lines);
        }

        [Fact]
        public void ClientLog_logs_each_event_and_answers_204()
        {
            TestPlugin.Configure(c => c.ClientDiagnostics = true);
            var (controller, log, user) = NewController();

            Assert.IsType<NoContentResult>(controller.ClientLog(Payload("first", "second")));

            Assert.Equal(2, log.Lines.Count);
            string[] lines = log.Lines.Select(l => l.Message).ToArray();
            Assert.Contains($"[hss-client] {user} build=b1 first | UA/1.0", lines);
            Assert.Contains($"[hss-client] {user} build=b1 second | UA/1.0", lines);
        }

        [Fact]
        public void ClientLog_takes_at_most_60_lines_per_post()
        {
            TestPlugin.Configure(c => c.ClientDiagnostics = true);
            var (controller, log, _) = NewController();

            controller.ClientLog(Payload(Enumerable.Range(0, 100).Select(i => "e" + i).ToArray()));

            Assert.Equal(60, log.Lines.Count);
            Assert.Contains(log.Lines, l => l.Message.Contains(" e59 "));
            Assert.DoesNotContain(log.Lines, l => l.Message.Contains(" e60 "));
        }

        [Fact]
        public void ClientLog_cuts_a_line_at_1600_characters_and_the_user_agent_at_140()
        {
            TestPlugin.Configure(c => c.ClientDiagnostics = true);
            var (controller, log, _) = NewController();

            controller.ClientLog(new HomeScreenController.ClientLogPayload { Build = "b", Ua = new string('u', 500), Events = new[] { new string('a', 5000) } });

            string message = Assert.Single(log.Lines).Message;
            Assert.Contains(new string('a', 1600), message);
            Assert.DoesNotContain(new string('a', 1601), message);
            Assert.Contains(new string('u', 140), message);
            Assert.DoesNotContain(new string('u', 141), message);
        }

        [Fact]
        public void ClientLog_strips_control_characters_so_a_client_cannot_forge_log_lines()
        {
            TestPlugin.Configure(c => c.ClientDiagnostics = true);
            var (controller, log, _) = NewController();

            controller.ClientLog(Payload("one\r\n[Information] fake\tline\u001b[31m\u0000end"));

            string message = Assert.Single(log.Lines).Message;
            Assert.DoesNotContain('\n', message);
            Assert.DoesNotContain('\r', message);
            Assert.DoesNotContain('\t', message);
            Assert.DoesNotContain('\u001b', message);
            Assert.DoesNotContain('\0', message);
            Assert.Contains("one[Information] fakeline[31mend", message);
        }

        [Fact]
        public void ClientLog_answers_429_after_30_posts_a_minute_per_user()
        {
            TestPlugin.Configure(c => c.ClientDiagnostics = true);
            var (controller, log, _) = NewController();

            for (int i = 0; i < 30; i++)
            {
                Assert.IsType<NoContentResult>(controller.ClientLog(Payload("x")));
            }

            StatusCodeResult limited = Assert.IsType<StatusCodeResult>(controller.ClientLog(Payload("x")));
            Assert.Equal(StatusCodes.Status429TooManyRequests, limited.StatusCode);
            Assert.Equal(30, log.Lines.Count); // the refused post logged nothing

            var (other, _, _) = NewController(); // somebody else is unaffected
            Assert.IsType<NoContentResult>(other.ClientLog(Payload("x")));
        }
    }
}
