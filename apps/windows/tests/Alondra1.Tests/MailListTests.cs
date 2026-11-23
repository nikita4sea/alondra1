using System.Text.RegularExpressions;
using Alondra1.Core.Config;
using Alondra1.Core.Mail;

namespace Alondra1.Tests;

/// <summary>Client spec M1: the list header count, the Settings gear, reading settings, and the reinstall test file.</summary>
public class MailListTests
{
    // ---------- Count (fixtures M13 / M14) ----------

    [Theory]
    [InlineData(24, 24, false, false, 0, "24 messages")]
    [InlineData(1, 1, false, false, 0, "1 message")]
    [InlineData(5, 24, false, true, 0, "5 of 24 messages")]
    [InlineData(0, 24, false, true, 0, "No matches")]
    [InlineData(0, 0, false, false, 0, "No messages")]
    [InlineData(0, null, true, false, 0, "Loading…")]
    [InlineData(24, null, true, false, 0, "24 loaded")]
    [InlineData(50, 50, false, false, 3, "3 selected · 50 messages")]
    [InlineData(50, 50, false, false, 1, "50 messages")]
    public void Count_describes_the_list_not_the_viewport(int shown, int? total, bool loading, bool filtered, int selected, string expected) =>
        Assert.Equal(expected, MessageCount.Describe(shown, total, loading, filtered, selected));

    [Fact]
    public void Count_never_says_zero_of_zero_while_rows_are_shown()
    {
        for (var shown = 1; shown <= 50; shown++)
        {
            foreach (var filtered in new[] { false, true })
            {
                var text = MessageCount.Describe(shown, total: 0, loading: false, filtered);
                Assert.DoesNotContain("0 of 0", text);
                Assert.StartsWith(shown.ToString(), text);
            }
        }
    }

    [Fact]
    public void Count_for_all_accounts_and_one_account_matches_the_rows()
    {
        // M14: two accounts, a search filter, combined and scoped views.
        var work = Enumerable.Range(0, 30).Select(i => $"work {i} {(i % 3 == 0 ? "invoice" : "note")}").ToList();
        var home = Enumerable.Range(0, 20).Select(i => $"home {i} {(i % 4 == 0 ? "invoice" : "photo")}").ToList();
        static int Match(IEnumerable<string> rows, string query) => rows.Count(row => row.Contains(query));

        Assert.Equal("50 messages", MessageCount.Describe(work.Count + home.Count, work.Count + home.Count, false, false));
        Assert.Equal($"{Match(work, "invoice") + Match(home, "invoice")} of 50 messages",
            MessageCount.Describe(Match(work, "invoice") + Match(home, "invoice"), 50, false, true));
        Assert.Equal($"{Match(work, "invoice")} of 30 messages", MessageCount.Describe(Match(work, "invoice"), 30, false, true));
        Assert.Equal("20 messages", MessageCount.Describe(home.Count, home.Count, false, false));
    }

    // ---------- Settings icon ----------

    [Fact]
    public void Settings_icon_is_a_gear_with_eight_teeth()
    {
        var icons = File.ReadAllText(Path.Combine(UiPaths.App, "Themes", "Icons.xaml"));
        var data = Regex.Match(icons, @"x:Key=""Icon\.Settings"">([^<]+)<").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(data));
        // An outline (teeth joined by root arcs) and a center hole: two closed figures, eight arcs between teeth,
        // and no loose spokes (open "M … V/L" strokes), which made the old icon read as a ship's wheel.
        Assert.Equal(2, Regex.Matches(data, "M").Count);
        Assert.Equal(2, Regex.Matches(data, "Z").Count);
        Assert.Equal(8, Regex.Matches(data, @"A7\.4 7\.4").Count);
        Assert.DoesNotContain("V", data);
    }

    [Fact]
    public void Settings_button_keeps_its_name_and_opens_settings()
    {
        var window = File.ReadAllText(Path.Combine(UiPaths.App, "MainWindow.xaml"));
        Assert.Matches(@"Command=""\{Binding SettingsCommand\}""[^>]*AutomationProperties\.Name=""Settings""[^>]*ToolTip=""Settings""", window);
        Assert.Contains(@"Kind=""Settings""", window);
    }

    [Fact]
    public void Disabled_message_actions_explain_why()
    {
        var window = File.ReadAllText(Path.Combine(UiPaths.App, "MainWindow.xaml"));
        foreach (var command in new[] { "ReplyCommand", "ReplyAllCommand", "ForwardCommand", "ArchiveCommand", "DeleteCommand", "ToggleReadCommand" })
            Assert.Matches($@"Command=""\{{Binding {command}\}}"" ui:ActionHint\.WhenDisabled=""Select a message first""", window);
        Assert.Contains(@"Style=""{StaticResource DangerIconButton}"" Command=""{Binding DeleteCommand}""", window);
        Assert.Single(Regex.Matches(window, @"Style=""\{StaticResource PrimaryButton\}"""));
    }

    // ---------- Reading settings ----------

    [Fact]
    public void Reading_defaults_prefer_html_and_ask_before_remote_images()
    {
        var config = new AppConfig();
        Assert.True(config.Reading.PreferHtml);
        Assert.Equal(RemoteImagePolicy.Ask, config.Reading.RemoteImages);
        Assert.Empty(config.Reading.TrustedSenders);
        var old = ConfigWorkflow.Parse("""{ "schemaVersion": 1 }""");
        Assert.True(old.Reading.PreferHtml);
        Assert.True(ConfigWorkflow.Validate(ConfigWorkflow.Parse("""{ "schemaVersion": 1, "reading": null }""")).Ok);
    }

    [Fact]
    public void Reading_settings_round_trip_and_reject_bad_senders()
    {
        var config = new AppConfig();
        config.Reading.PreferHtml = false;
        config.Reading.RemoteImages = RemoteImagePolicy.Always;
        config.Reading.TrustedSenders.Add("news@example.com");
        var copy = ConfigWorkflow.Parse(ConfigWorkflow.ExportJson(config));
        Assert.False(copy.Reading.PreferHtml);
        Assert.Equal(RemoteImagePolicy.Always, copy.Reading.RemoteImages);
        Assert.Equal(["news@example.com"], copy.Reading.TrustedSenders);

        config.Reading.TrustedSenders.Add("not an address");
        Assert.False(ConfigWorkflow.Validate(config).Ok);
    }

    [Fact]
    public void Import_carries_reading_settings()
    {
        var incoming = new AppConfig();
        incoming.Reading.RemoteImages = RemoteImagePolicy.Always;
        var plan = ConfigWorkflow.Preview(new AppConfig(), incoming);
        Assert.Equal(RemoteImagePolicy.Always, plan.Merged!.Reading.RemoteImages);
        Assert.Equal(RemoteImagePolicy.Always, plan.Replaced!.Reading.RemoteImages);
    }

    [Fact]
    public void Remembered_mail_account_survives_a_save()
    {
        var config = new AppConfig();
        config.Layout.MailAccount = "Dev (IMAP)";
        Assert.Equal("Dev (IMAP)", ConfigWorkflow.Parse(ConfigWorkflow.ExportJson(config)).Layout.MailAccount);
    }

    // ---------- Reinstall test file ----------

    [Fact]
    public void Test_account_file_yields_address_and_password_pairs_only()
    {
        var pairs = TestAccountFile.ParsePasswords(
        [
            "USER1=dev@example.com",
            "PASSWORD1=secret=with=equals",
            "USER2 = user@example.com ",
            "PASSWORD2=two",
            "USER3=lonely@example.com",
            "IMAP=imap.example.com:993",
            "# comment",
            "",
        ]);
        Assert.Equal([("dev@example.com", "secret=with=equals"), ("user@example.com", "two")], pairs);
    }
}

internal static class UiPaths
{
    public static string App
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Alondra1.App")))
                directory = directory.Parent;
            return Path.Combine(directory!.FullName, "src", "Alondra1.App");
        }
    }
}
