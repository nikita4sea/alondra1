using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Alondra1.App.Shell;
using Alondra1.App.ViewModels;
using Alondra1.Core.Config;
using Alondra1.Core.Mail;
using Alondra1.Core.Security;
using Alondra1.Core.Sync;
using Microsoft.Win32;

namespace Alondra1.App;

/// <summary>
/// Alondra1's secondary windows. They share one frame (<see cref="Create"/>): a native title bar that follows the theme,
/// a scrolling body with standard padding, and a fixed footer for actions. Controls come from the shared styles in
/// Themes/Controls.xaml, so a control created here looks like the same control in the main window.
/// </summary>
public static class Dialogs
{
    private sealed class Parts
    {
        public required Grid Root { get; init; }
        public required ContentControl Body { get; init; }
        public required Border Footer { get; init; }
        public required TextBlock Hint { get; init; }
        public required StackPanel Actions { get; init; }
    }

    private static readonly ConditionalWeakTable<Window, Parts> Frames = new();

    // ---------- Welcome ----------

    public static void ShowWelcome(Window owner, MainViewModel model)
    {
        var window = Create("Alondra1", owner, 440, sizeToContent: true);
        var panel = Panel(window);
        panel.Children.Add(Title("Set up Alondra1"));
        panel.Children.Add(Note("Add a mailbox or import your configuration to get started."));
        var actions = Footer(window);
        actions.Children.Add(Button("Import configuration", () =>
        {
            window.Close();
            ShowImport(owner, model);
        }));
        actions.Children.Add(Primary("Add an account", () =>
        {
            window.Close();
            ShowAccount(owner, model, null);
        }));
        window.ShowDialog();
    }

    // ---------- Settings ----------

    public static void ShowSettings(Window owner, MainViewModel model, int page = 0)
    {
        var window = Create("Settings", owner, 800, 640, scroll: false);
        window.MinWidth = 620;
        window.MinHeight = 480;

        // Appearance
        var appearance = Page();
        appearance.Children.Add(Title("Appearance"));
        var theme = new ComboBox { ItemsSource = Enum.GetNames<ThemePreference>(), SelectedItem = model.Config.Appearance.Theme.ToString(), Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        appearance.Children.Add(Labeled("Theme", theme));
        appearance.Children.Add(Subtitle("Colors"));
        appearance.Children.Add(Note("Adjust the palette of either theme. Alondra1 checks that each value is a color; check that text stays readable."));
        appearance.Children.Add(ButtonRow(
            Button("Edit light colors", () => EditPalette(window, model, true)),
            Button("Edit dark colors", () => EditPalette(window, model, false))));

        // Background
        var background = new BackgroundEditor(window, model);
        var backgroundPage = Page();
        backgroundPage.Children.Add(background.Build());

        // Accounts
        var accounts = Page();
        accounts.Children.Add(Title("Accounts"));
        if (model.Config.Accounts.Count == 0)
            accounts.Children.Add(Note("No accounts yet."));
        foreach (var account in model.Config.Accounts.ToList())
        {
            var card = new Border { Style = (Style)Application.Current.FindResource("Card"), Padding = new Thickness(14, 10, 10, 10), Margin = new Thickness(0, 0, 0, 8) };
            var line = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right);
            buttons.Children.Add(Button("Edit", () => ShowAccount(window, model, account)));
            var target = account.Mode == AccountMode.Personal ? AccountMode.Online : AccountMode.Personal;
            buttons.Children.Add(Button(target == AccountMode.Personal ? "Make Personal" : "Make Online", async () =>
            {
                try
                {
                    var discard = false;
                    if (target == AccountMode.Online)
                    {
                        var answer = Alert(window, "Personal mode keeps a local copy and an outbox. Switch to Online only after you export anything you want to keep. Discard pending sends and remove the local copy?", "Alondra1", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (answer != MessageBoxResult.Yes)
                            return;
                        discard = true;
                    }

                    await model.ChangeModeAsync(account, target, discard);
                    window.Close();
                }
                catch (Exception ex)
                {
                    Alert(window, ex.Message, "Alondra1");
                }
            }));
            buttons.Children.Add(Button("Delete", async () =>
            {
                try
                {
                    var pending = await model.PendingSendCountAsync(account);
                    var message = $"Delete {account.DisplayName} ({account.Address}) from Alondra1?\n\n"
                        + "This removes the account's settings, saved password and sign-in, and everything Alondra1 keeps for it on this PC: the local mail copy, attachment files, and outbox."
                        + (pending > 0 ? $"\n\n{pending} unsent message{(pending == 1 ? "" : "s")} in the outbox will be discarded." : "")
                        + "\n\nMail on the server is not changed.";
                    if (Alert(window, message, "Delete account", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return;
                    await model.DeleteAccountAsync(account);
                    window.Close();
                }
                catch (Exception ex)
                {
                    Alert(window, ex.Message, "Alondra1");
                }
            }));
            line.Children.Add(buttons);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = account.DisplayName, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            text.Children.Add(new TextBlock { Text = $"{account.Address} · {account.Mode}", Style = (Style)Application.Current.FindResource("HelperText") });
            line.Children.Add(text);
            card.Child = line;
            accounts.Children.Add(card);
        }

        accounts.Children.Add(ButtonRow(Button("Add an account", () => ShowAccount(window, model, null))));

        // Connections (OAuth)
        var connections = Page();
        connections.Children.Add(Title("Connections"));
        connections.Children.Add(Note("Enter public client ids only. Alondra1 never stores a client secret."));
        var microsoft = Field(model.Config.OAuth.MicrosoftClientId);
        var tenant = Field(model.Config.OAuth.MicrosoftTenant);
        var google = Field(model.Config.OAuth.GoogleClientId);
        connections.Children.Add(Row("Microsoft client id", microsoft, 150));
        connections.Children.Add(Row("Microsoft tenant", tenant, 150));
        connections.Children.Add(Row("Google client id", google, 150));
        connections.Children.Add(Subtitle("Features"));
        var calendar = Check("Calendar", model.Config.OAuth.Calendar);
        var tasks = Check("To-do", model.Config.OAuth.Tasks);
        var contacts = Check("Contacts", model.Config.OAuth.Contacts);
        var notes = Check("Notes", model.Config.OAuth.Notes);
        connections.Children.Add(calendar);
        connections.Children.Add(tasks);
        connections.Children.Add(contacts);
        connections.Children.Add(notes);
        connections.Children.Add(Note("These permissions are requested only after you enable the feature and sign in."));

        // Data
        var data = Page();
        data.Children.Add(Title("Data"));
        data.Children.Add(Subtitle("Configuration", first: true));
        data.Children.Add(ButtonRow(
            Button("Import…", () => ShowImport(window, model)),
            Button("Export…", () =>
            {
                var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "alondra1-config.json" };
                if (dialog.ShowDialog(window) == true)
                {
                    var json = model.ExportTo(dialog.FileName);
                    var secrets = ConfigWorkflow.FindSecretMaterial(json);
                    Alert(window, secrets.Count == 0 ? "Exported settings. No secrets were included." : "Export contained unexpected secret fields.", "Alondra1");
                }
            })));
        data.Children.Add(Subtitle("Storage"));
        var usage = model.DiskUsage();
        data.Children.Add(Note($"Personal database {usage.Database / 1024} KB · attachment files {usage.Files / 1024} KB"));
        if (model.Config.Accounts.FirstOrDefault() is { } first)
        {
            data.Children.Add(ButtonRow(Button("Remove unpinned attachment files", async () =>
            {
                await model.CleanupAttachmentsAsync(first.Id);
                Alert(window, "Unpinned attachment files were removed.", "Alondra1");
            })));
        }

        // Layout: section list on the left, the chosen section on the right; only the section scrolls.
        var pages = new (string Name, string Icon, UIElement Page)[]
        {
            ("Appearance", "Settings", appearance), ("Background", "Image", backgroundPage), ("Accounts", "Contacts", accounts),
            ("Connections", "Refresh", connections), ("Data", "Archive", data),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var navBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(10, 16, 10, 16) };
        navBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        navBorder.SetResourceReference(Border.BackgroundProperty, "SubtleBrush");
        var nav = new ListBox { ItemContainerStyle = (Style)Application.Current.FindResource("TabItem") };
        System.Windows.Automation.AutomationProperties.SetName(nav, "Settings sections");
        foreach (var (name, icon, _) in pages)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            item.Children.Add(new Controls.Alondra1Icon { Kind = icon, IconSize = 18, VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = name, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            System.Windows.Automation.AutomationProperties.SetName(item, name);
            nav.Items.Add(new ListBoxItem { Content = item, Margin = new Thickness(0, 0, 0, 2), HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }

        navBorder.Child = nav;
        grid.Children.Add(navBorder);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        nav.SelectionChanged += (_, _) =>
        {
            if (nav.SelectedIndex < 0)
                return;
            scroll.Content = pages[nav.SelectedIndex].Page;
            scroll.ScrollToTop();
        };
        nav.SelectedIndex = Math.Clamp(page, 0, pages.Length - 1);
        Frames.TryGetValue(window, out var parts);
        parts!.Body.Content = grid;

        var footer = Footer(window);
        footer.Children.Add(Cancel(window));
        footer.Children.Add(Primary("Save settings", () =>
        {
            if (Enum.TryParse<ThemePreference>(theme.SelectedItem as string, out var selected))
                model.Config.Appearance.Theme = selected;
            model.Config.OAuth.MicrosoftClientId = microsoft.Text.Trim();
            model.Config.OAuth.MicrosoftTenant = string.IsNullOrWhiteSpace(tenant.Text) ? "common" : tenant.Text.Trim();
            model.Config.OAuth.GoogleClientId = google.Text.Trim();
            model.Config.OAuth.Calendar = calendar.IsChecked == true;
            model.Config.OAuth.Tasks = tasks.IsChecked == true;
            model.Config.OAuth.Contacts = contacts.IsChecked == true;
            model.Config.OAuth.Notes = notes.IsChecked == true;
            var validation = ConfigWorkflow.Validate(model.Config);
            if (!validation.Ok)
            {
                Alert(window, string.Join("\n", validation.Errors), "Alondra1");
                return;
            }

            background.Commit();
            model.ConfigStoreSave();
            ThemeService.Apply(model.Config.Appearance);
            (owner as MainWindow)?.ApplyBackground();
            model.ServicesRefresh();
            window.Close();
        }));
        window.ShowDialog();

        static StackPanel Page() => new() { Margin = new Thickness(24, 20, 24, 24), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
    }

    // ---------- Import ----------

    public static void ShowImport(Window owner, MainViewModel model)
    {
        var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dialog.ShowDialog(owner) != true)
            return;
        AppConfig incoming;
        try
        {
            incoming = ConfigWorkflow.Parse(File.ReadAllText(dialog.FileName));
        }
        catch (ConfigFormatException ex)
        {
            Alert(owner, ex.Message + "\nThe current setup was not changed.", "Alondra1");
            return;
        }

        var plan = ConfigWorkflow.Preview(model.Config, incoming);
        if (!plan.Ok || plan.Merged is null || plan.Replaced is null)
        {
            Alert(owner, string.Join("\n", plan.Errors) + "\nThe current setup was not changed.", "Alondra1");
            return;
        }

        var window = Create("Import configuration", owner, 500, sizeToContent: true);
        var panel = Panel(window);
        panel.Children.Add(Title("Preview"));
        panel.Children.Add(Note(string.Join("\n", plan.Additions.Concat(plan.Changes).Concat(plan.Warnings).DefaultIfEmpty("No account changes."))));
        var actions = Footer(window);
        actions.Children.Add(Cancel(window));
        actions.Children.Add(Button("Replace after review", () =>
        {
            if (Alert(window, "Replace the account list with this file? Existing account modes stay as they are, and saved mail is not deleted.", "Alondra1", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
            {
                model.ApplyImport(plan.Replaced, replace: true);
                ThemeService.Apply(model.Config.Appearance);
                window.Close();
            }
        }));
        actions.Children.Add(Primary("Merge", () =>
        {
            model.ApplyImport(plan.Merged, replace: false);
            ThemeService.Apply(model.Config.Appearance);
            window.Close();
        }));
        window.ShowDialog();
    }

    // ---------- Account ----------

    public static void ShowAccount(Window owner, MainViewModel model, AccountConfig? existing)
    {
        var account = existing is null ? new AccountConfig { Id = Guid.NewGuid().ToString("n"), DisplayName = "", Address = "" } : ConfigWorkflow.Clone(model.Config).Accounts.First(item => item.Id == existing.Id);
        var window = Create(existing is null ? "Add account" : "Edit account", owner, 580, 680);
        window.MinWidth = 460;
        window.MinHeight = 420;
        var panel = Panel(window);
        var kind = new ComboBox { ItemsSource = Enum.GetNames<AccountKind>(), SelectedItem = account.Kind.ToString() };
        var mode = new ComboBox { ItemsSource = Enum.GetNames<AccountMode>(), SelectedItem = account.Mode.ToString() };
        var protocol = new ComboBox { ItemsSource = Enum.GetNames<MailProtocol>(), SelectedItem = account.Incoming.Protocol.ToString() };
        var name = Field(account.DisplayName);
        var address = Field(account.Address);
        var inHost = Field(account.Incoming.Host);
        var inPort = Field(account.Incoming.Port.ToString());
        var inSecurity = new ComboBox { ItemsSource = Enum.GetNames<TransportSecurity>(), SelectedItem = account.Incoming.Security.ToString() };
        var inUser = Field(account.Incoming.Username);
        var inAuth = new ComboBox { ItemsSource = Enum.GetNames<AuthenticationKind>(), SelectedItem = account.Incoming.Authentication.ToString() };
        var outHost = Field(account.Outgoing.Host);
        var outPort = Field(account.Outgoing.Port.ToString());
        var outSecurity = new ComboBox { ItemsSource = Enum.GetNames<TransportSecurity>(), SelectedItem = account.Outgoing.Security.ToString() };
        var outUser = Field(account.Outgoing.Username);
        var outAuth = new ComboBox { ItemsSource = Enum.GetNames<AuthenticationKind>(), SelectedItem = account.Outgoing.Authentication.ToString() };
        var password = new PasswordBox();
        var plaintext = Check("I understand that an unencrypted server can expose message content and the password.", account.PlaintextConfirmed);
        panel.Children.Add(Subtitle("Account", first: true));
        panel.Children.Add(Row("Provider", kind));
        panel.Children.Add(Row("Mode", mode));
        panel.Children.Add(Row("Name", name));
        panel.Children.Add(Row("Address", address));
        panel.Children.Add(Row("Password", password));
        panel.Children.Add(Subtitle("Incoming mail"));
        panel.Children.Add(Row("Receive with", protocol));
        panel.Children.Add(Row("Server", inHost));
        panel.Children.Add(Row("Port", inPort));
        panel.Children.Add(Row("Security", inSecurity));
        panel.Children.Add(Row("User name", inUser));
        panel.Children.Add(Row("Sign-in", inAuth));
        panel.Children.Add(Subtitle("Outgoing mail"));
        panel.Children.Add(Row("Server", outHost));
        panel.Children.Add(Row("Port", outPort));
        panel.Children.Add(Row("Security", outSecurity));
        panel.Children.Add(Row("User name", outUser));
        panel.Children.Add(Row("Sign-in", outAuth));
        plaintext.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(plaintext);
        kind.SelectionChanged += (_, _) => ApplyKind();
        protocol.SelectionChanged += (_, _) => { ApplyKind(); MatchIncomingPortToProtocol(); };
        // Keep port and security consistent: a standard port selects its security, and a security change moves a
        // standard port to the matching one (for example STARTTLS 587 <-> Implicit TLS 465).
        inPort.TextChanged += (_, _) => MatchSecurityToPort(inPort, inSecurity);
        outPort.TextChanged += (_, _) => MatchSecurityToPort(outPort, outSecurity);
        inSecurity.SelectionChanged += (_, _) => MatchPortToSecurity(incoming: true);
        outSecurity.SelectionChanged += (_, _) => MatchPortToSecurity(incoming: false);
        var actions = Footer(window);
        actions.Children.Add(Button("Test connection", async () =>
        {
            if (!TryCopy(account, out var error))
            {
                Alert(window, error, "Alondra1");
                return;
            }

            if (!ConfirmPlaintext(window, account, plaintext.IsChecked == true))
                return;
            account.PlaintextConfirmed = !ConfigWorkflow.UsesPlaintext(account) || plaintext.IsChecked == true;
            var result = await model.TestAccountAsync(account, password.Password);
            Alert(window, result, "Alondra1");
        }));
        actions.Children.Add(Cancel(window));
        actions.Children.Add(Primary("Save account", () =>
        {
            if (!TryCopy(account, out var error))
            {
                Alert(window, error, "Alondra1");
                return;
            }

            if (ConfigWorkflow.UsesPlaintext(account) && plaintext.IsChecked != true)
            {
                Alert(window, ConnectionPolicy.PlaintextWarning, "Alondra1");
                return;
            }

            account.PlaintextConfirmed = !ConfigWorkflow.UsesPlaintext(account) || plaintext.IsChecked == true;
            var validation = ConfigWorkflow.Validate(new AppConfig { Accounts = [account], OAuth = model.Config.OAuth });
            if (!validation.Ok)
            {
                Alert(window, string.Join("\n", validation.Errors), "Alondra1");
                return;
            }

            model.UpsertAccount(account, string.IsNullOrEmpty(password.Password) ? null : password.Password);
            window.Close();
        }));
        if (existing is null)
            ApplyKind();
        window.ShowDialog();

        void MatchIncomingPortToProtocol()
        {
            if (!Enum.TryParse<MailProtocol>(protocol.SelectedItem as string, out var selectedProtocol)
                || !Enum.TryParse<TransportSecurity>(inSecurity.SelectedItem as string, out var security)
                || !int.TryParse(inPort.Text, out var port) || !PortAdvisor.IsStandardIncomingPort(port))
                return;
            inPort.Text = PortAdvisor.DefaultIncomingPort(selectedProtocol, security).ToString();
        }

        void MatchSecurityToPort(TextBox portBox, ComboBox securityBox)
        {
            if (int.TryParse(portBox.Text, out var port) && PortAdvisor.SecurityFor(port) is { } expected
                && (securityBox.SelectedItem as string) != expected.ToString()
                && !((securityBox.SelectedItem as string) == TransportSecurity.None.ToString() && expected == TransportSecurity.RequiredStartTls))
                securityBox.SelectedItem = expected.ToString();
        }

        void MatchPortToSecurity(bool incoming)
        {
            var portBox = incoming ? inPort : outPort;
            var securityBox = incoming ? inSecurity : outSecurity;
            if (!Enum.TryParse<TransportSecurity>(securityBox.SelectedItem as string, out var security) || !int.TryParse(portBox.Text, out var port))
                return;
            // Only a contradiction moves the port: 25 with STARTTLS or 143 with None stay as typed.
            var expected = PortAdvisor.SecurityFor(port);
            if (expected is null || expected == security || (security == TransportSecurity.None && expected == TransportSecurity.RequiredStartTls))
                return;
            if (incoming && PortAdvisor.IsStandardIncomingPort(port) && Enum.TryParse<MailProtocol>(protocol.SelectedItem as string, out var selectedProtocol))
                portBox.Text = PortAdvisor.DefaultIncomingPort(selectedProtocol, security).ToString();
            else if (!incoming && PortAdvisor.IsStandardOutgoingPort(port))
                portBox.Text = PortAdvisor.DefaultOutgoingPort(security).ToString();
        }

        void ApplyKind()
        {
            if (!Enum.TryParse<AccountKind>(kind.SelectedItem as string, out var selectedKind))
                return;
            if (!Enum.TryParse<MailProtocol>(protocol.SelectedItem as string, out var selectedProtocol))
                return;
            account.Kind = selectedKind;
            if (selectedKind == AccountKind.Microsoft365)
                ProviderPresets.ApplyMicrosoft(account, selectedProtocol);
            else if (selectedKind == AccountKind.Gmail)
                ProviderPresets.ApplyGmail(account, selectedProtocol);
            else
                return;
            inHost.Text = account.Incoming.Host;
            inPort.Text = account.Incoming.Port.ToString();
            inSecurity.SelectedItem = account.Incoming.Security.ToString();
            inAuth.SelectedItem = account.Incoming.Authentication.ToString();
            outHost.Text = account.Outgoing.Host;
            outPort.Text = account.Outgoing.Port.ToString();
            outSecurity.SelectedItem = account.Outgoing.Security.ToString();
            outAuth.SelectedItem = account.Outgoing.Authentication.ToString();
        }

        bool TryCopy(AccountConfig target, out string error)
        {
            error = "";
            if (!Enum.TryParse<AccountKind>(kind.SelectedItem as string, out var selectedKind)
                || !Enum.TryParse<AccountMode>(mode.SelectedItem as string, out var selectedMode)
                || !Enum.TryParse<MailProtocol>(protocol.SelectedItem as string, out var selectedProtocol)
                || !Enum.TryParse<TransportSecurity>(inSecurity.SelectedItem as string, out var incomingSecurity)
                || !Enum.TryParse<TransportSecurity>(outSecurity.SelectedItem as string, out var outgoingSecurity)
                || !Enum.TryParse<AuthenticationKind>(inAuth.SelectedItem as string, out var incomingAuth)
                || !Enum.TryParse<AuthenticationKind>(outAuth.SelectedItem as string, out var outgoingAuth)
                || !int.TryParse(inPort.Text, out var incomingPort)
                || !int.TryParse(outPort.Text, out var outgoingPort))
            {
                error = "Check the account fields.";
                return false;
            }

            var incoming = new IncomingSettings
            {
                Protocol = selectedProtocol,
                Host = inHost.Text.Trim(),
                Port = incomingPort,
                Security = incomingSecurity,
                Username = string.IsNullOrWhiteSpace(inUser.Text) ? address.Text.Trim() : inUser.Text.Trim(),
                Authentication = incomingAuth,
            };
            var outgoing = new OutgoingSettings
            {
                Host = outHost.Text.Trim(),
                Port = outgoingPort,
                Security = outgoingSecurity,
                Username = string.IsNullOrWhiteSpace(outUser.Text) ? address.Text.Trim() : outUser.Text.Trim(),
                Authentication = outgoingAuth,
            };
            if (existing is not null && (ConfigWorkflow.UsesPlaintext(new AccountConfig { Incoming = incoming, Outgoing = outgoing }) && (!string.Equals(existing.Incoming.Host, incoming.Host, StringComparison.OrdinalIgnoreCase) || existing.Incoming.Security != incoming.Security || existing.Outgoing.Security != outgoing.Security)))
                target.PlaintextConfirmed = false;
            target.Kind = selectedKind;
            target.Mode = existing?.Mode ?? selectedMode;
            target.DisplayName = name.Text.Trim();
            target.Address = address.Text.Trim();
            ConfigWorkflow.ApplyTransportEdit(target, incoming, outgoing);
            if (selectedKind == AccountKind.Manual && target.Folders.Sent is null)
            {
                target.Folders.Sent = "Sent";
                target.Folders.Drafts = "Drafts";
                target.Folders.Trash = "Trash";
                target.Folders.Archive = "Archive";
            }

            return true;
        }
    }

    // ---------- New message ----------

    public static void ShowCompose(Window owner, MainViewModel model, OutgoingMail? seed)
    {
        var mail = seed ?? new OutgoingMail();
        var window = Create(string.IsNullOrWhiteSpace(mail.Subject) ? "New message" : mail.Subject, owner, 720, 560, scroll: false);
        window.MinWidth = 460;
        window.MinHeight = 360;
        var accounts = model.IsDemo ? ["Work"] : model.Config.Accounts.Select(account => account.DisplayName).ToList();
        var from = new ComboBox { ItemsSource = accounts, SelectedIndex = 0 };
        var to = Field(string.Join(", ", mail.To));
        var subject = Field(mail.Subject);
        var body = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Text = mail.TextBody,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 12, 0, 0),
            MinHeight = 120,
        };
        System.Windows.Automation.AutomationProperties.SetName(from, "From");
        System.Windows.Automation.AutomationProperties.SetName(to, "To");
        System.Windows.Automation.AutomationProperties.SetName(subject, "Subject");
        System.Windows.Automation.AutomationProperties.SetName(body, "Message");

        // From, To, and Subject share one label column; the editor takes the remaining height.
        var layout = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 3; i++)
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        AddRow(0, "From", from);
        AddRow(1, "To", to);
        AddRow(2, "Subject", subject);
        Grid.SetRow(body, 3);
        Grid.SetColumnSpan(body, 2);
        layout.Children.Add(body);
        Frames.TryGetValue(window, out var parts);
        parts!.Body.Content = layout;

        var account = model.Config.Accounts.FirstOrDefault();
        if (account?.Incoming.Protocol == MailProtocol.Pop3 && account.Mode == AccountMode.Online)
            Hint(window, "Draft kept for this session only", "This POP3 account keeps the draft in memory for this session. The server does not store drafts.");
        var actions = Footer(window);
        actions.Children.Add(Button("Save draft", async () =>
        {
            if (model.IsDemo)
            {
                Alert(window, "Sample data is not saved.", "Alondra1");
                return;
            }

            Fill(mail, from, to, subject, body);
            var selected = model.Config.Accounts.FirstOrDefault(item => item.DisplayName == from.SelectedItem as string) ?? model.Config.Accounts.FirstOrDefault();
            if (selected is null)
                return;
            await model.SaveDraft(selected, mail);
            window.Close();
        }));
        actions.Children.Add(Primary("Send", async () =>
        {
            if (model.IsDemo)
            {
                Alert(window, "Demo mode does not send mail.", "Alondra1");
                return;
            }

            Fill(mail, from, to, subject, body);
            var selected = model.Config.Accounts.FirstOrDefault(item => item.DisplayName == from.SelectedItem as string);
            if (selected is null)
                return;
            var result = await model.Send(selected, mail);
            Alert(window, result.Acceptance + (result.Error is null ? "" : "\n" + result.Error), "Alondra1");
            window.Close();
        }));
        window.Loaded += (_, _) => (to.Text.Length == 0 ? to : body).Focus();
        window.ShowDialog();

        void AddRow(int row, string label, Control control)
        {
            var text = new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("FieldLabel") };
            Grid.SetRow(text, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            control.Margin = new Thickness(0, row == 0 ? 0 : 8, 0, 0);
            text.Margin = new Thickness(0, row == 0 ? 0 : 8, 0, 0);
            layout.Children.Add(text);
            layout.Children.Add(control);
        }
    }

    private static void Fill(OutgoingMail mail, ComboBox from, TextBox to, TextBox subject, TextBox body)
    {
        mail.To = to.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        mail.Subject = subject.Text;
        mail.TextBody = body.Text;
        mail.FromName = from.SelectedItem as string ?? "";
    }

    private static bool ConfirmPlaintext(Window owner, AccountConfig account, bool confirmed)
    {
        if (!ConfigWorkflow.UsesPlaintext(account))
            return true;
        if (confirmed)
            return true;
        return Alert(owner, ConnectionPolicy.PlaintextWarning, "Alondra1", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    // ---------- Palette ----------

    private static void EditPalette(Window owner, MainViewModel model, bool light)
    {
        var window = Create(light ? "Light colors" : "Dark colors", owner, 460, 620);
        var panel = Panel(window);
        panel.Children.Add(Note("Enter colors as #RRGGBB. Check that text remains readable against the surfaces and highlights you choose."));
        var appearance = model.Config.Appearance;
        var colors = (light ? appearance.LightColors : appearance.DarkColors) ?? [];
        var defaults = ThemeService.Defaults(light);
        var labels = new Dictionary<string, string>
        {
            ["SurfaceBrush"] = "Panels", ["ChromeBrush"] = "Window", ["TextBrush"] = "Text",
            ["MutedBrush"] = "Secondary text", ["LineBrush"] = "Borders", ["BlueBrush"] = "Accent",
            ["SelectedBrush"] = "Selection", ["HoverBrush"] = "Hover", ["DangerBrush"] = "Error text",
        };
        var fields = labels.ToDictionary(pair => pair.Key, pair =>
        {
            var fallback = ((SolidColorBrush)defaults[pair.Key]).Color.ToString()[3..];
            var input = Field(colors.TryGetValue(pair.Key, out var value) && ThemeService.IsHexColor(value) ? value : "#" + fallback);
            panel.Children.Add(Row(pair.Value, input, 130));
            return input;
        });
        void Apply(Dictionary<string, string> updated)
        {
            if (light)
                appearance.LightColors = updated;
            else
                appearance.DarkColors = updated;
            model.ConfigStoreSave();
            ThemeService.Apply(appearance);
            window.Close();
        }

        var actions = Footer(window);
        actions.Children.Add(Button("Restore defaults", () => Apply([])));
        actions.Children.Add(Cancel(window));
        actions.Children.Add(Primary("Apply colors", () =>
        {
            var updated = fields.ToDictionary(pair => pair.Key, pair => pair.Value.Text.Trim());
            if (updated.Values.All(ThemeService.IsHexColor))
                Apply(updated);
            else
                Alert(window, "Use six hexadecimal digits after # in each color.", "Alondra1");
        }));
        window.ShowDialog();
    }

    // ---------- Themed message box ----------

    /// <summary>
    /// A message box in Alondra1's style (themed title bar, shared buttons) that returns the same results as
    /// <see cref="MessageBox.Show(string)"/>. Falls back to the system message box before the app's resources are ready.
    /// </summary>
    public static MessageBoxResult Alert(Window? owner, string message, string title = "Alondra1", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? Application.Current?.MainWindow;
        if (owner is null || !owner.IsLoaded || Application.Current?.TryFindResource("SecondaryButton") is null)
            return owner is null ? MessageBox.Show(message, title, buttons, image) : MessageBox.Show(owner, message, title, buttons, image);

        var result = buttons is MessageBoxButton.OK ? MessageBoxResult.OK : buttons is MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
        var window = Create(title, owner, 440, sizeToContent: true);
        window.ResizeMode = ResizeMode.NoResize;
        var panel = Panel(window);
        var line = new DockPanel();
        if (image is MessageBoxImage.Warning or MessageBoxImage.Error or MessageBoxImage.Question)
        {
            var icon = new Controls.Alondra1Icon { Kind = image == MessageBoxImage.Question ? "Alert" : "Alert", IconSize = 24, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            icon.SetResourceReference(Controls.Alondra1Icon.ForegroundProperty, image == MessageBoxImage.Error ? "DangerBrush" : "StarBrush");
            DockPanel.SetDock(icon, Dock.Left);
            line.Children.Add(icon);
        }

        line.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 20 });
        panel.Children.Add(line);
        var actions = Footer(window);
        Button Choice(string label, MessageBoxResult value, bool primary, bool cancel = false)
        {
            var button = primary ? Primary(label, Close) : Button(label, Close);
            button.IsDefault = primary;
            button.IsCancel = cancel;
            return button;

            void Close()
            {
                result = value;
                window.Close();
            }
        }

        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                actions.Children.Add(Choice("Cancel", MessageBoxResult.Cancel, false, cancel: true));
                actions.Children.Add(Choice("OK", MessageBoxResult.OK, true));
                break;
            case MessageBoxButton.YesNo:
                actions.Children.Add(Choice("No", MessageBoxResult.No, false, cancel: true));
                actions.Children.Add(Choice("Yes", MessageBoxResult.Yes, true));
                break;
            case MessageBoxButton.YesNoCancel:
                actions.Children.Add(Choice("Cancel", MessageBoxResult.Cancel, false, cancel: true));
                actions.Children.Add(Choice("No", MessageBoxResult.No, false));
                actions.Children.Add(Choice("Yes", MessageBoxResult.Yes, true));
                break;
            default:
                var ok = Choice("OK", MessageBoxResult.OK, true);
                ok.IsCancel = true;
                actions.Children.Add(ok);
                break;
        }

        window.ShowDialog();
        return result;
    }

    public static MessageBoxResult Alert(string message, string title = "Alondra1", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) =>
        Alert(null, message, title, buttons, image);

    // ---------- Shared dialog frame and building blocks ----------

    /// <summary>
    /// Creates a Alondra1 dialog: themed native title bar, a body (scrolling unless <paramref name="scroll"/> is false)
    /// and a footer for actions that appears once <see cref="Footer"/> is used.
    /// </summary>
    internal static Window Create(string title, Window owner, double width, double height = 640, bool scroll = true, bool sizeToContent = false)
    {
        var body = scroll
            ? new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false }
            : (ContentControl)new ContentControl { Focusable = false };
        var hint = new TextBlock { Style = (Style)Application.Current.FindResource("HelperText"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 16, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var footerContent = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        footerContent.Children.Add(actions);
        footerContent.Children.Add(hint);
        var footer = new Border { Style = (Style)Application.Current.FindResource("DialogFooter"), Child = footerContent, Visibility = Visibility.Collapsed };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = sizeToContent ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(body);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        var window = new Window
        {
            Title = title,
            Width = width,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            ShowInTaskbar = false,
            UseLayoutRounding = true,
            Content = root,
        };
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        if (sizeToContent)
        {
            window.SizeToContent = SizeToContent.Height;
            window.MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 80);
        }
        else
        {
            window.Height = Math.Min(height, Math.Max(360, SystemParameters.WorkArea.Height - 40));
        }

        window.SetResourceReference(Control.BackgroundProperty, "ChromeBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        ShellWindow.ThemeCaption(window);
        Frames.Add(window, new Parts { Root = root, Body = body, Footer = footer, Hint = hint, Actions = actions });
        return window;
    }

    /// <summary>The dialog's main content area, with standard padding.</summary>
    internal static StackPanel Panel(Window window)
    {
        var panel = new StackPanel { Margin = (Thickness)Application.Current.FindResource("DialogPadding") };
        Frames.TryGetValue(window, out var parts);
        parts!.Body.Content = panel;
        return panel;
    }

    /// <summary>The right-aligned action area at the bottom of the dialog. Add the primary action last.</summary>
    internal static StackPanel Footer(Window window)
    {
        Frames.TryGetValue(window, out var parts);
        parts!.Footer.Visibility = Visibility.Visible;
        return parts.Actions;
    }

    /// <summary>Short secondary text on the left of the footer; the full explanation is its tooltip.</summary>
    internal static void Hint(Window window, string text, string? detail = null)
    {
        Frames.TryGetValue(window, out var parts);
        parts!.Footer.Visibility = Visibility.Visible;
        parts.Hint.Text = text;
        parts.Hint.ToolTip = detail;
        if (detail is not null)
            System.Windows.Automation.AutomationProperties.SetHelpText(parts.Hint, detail);
    }

    internal static TextBlock Title(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };

    /// <summary>A section heading inside a dialog page, with the standard 20 px gap above it.</summary>
    internal static TextBlock Subtitle(string text, bool first = false) =>
        new() { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, first ? 0 : 20, 0, 8) };

    internal static TextBlock Note(string text) => new() { Text = text, Style = (Style)Application.Current.FindResource("HelperText"), Margin = new Thickness(0, 0, 0, 12) };

    internal static TextBox Field(string text) => new() { Text = text };

    /// <summary>A label above a control (for wide controls and short forms).</summary>
    internal static UIElement Labeled(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("FieldLabel"), Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        return panel;
    }

    /// <summary>A label beside a control; rows in the same form share the label column width.</summary>
    internal static UIElement Row(string label, Control control, double labelWidth = 120)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("FieldLabel"), Margin = new Thickness(0, 0, 12, 0) });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        return grid;
    }

    internal static CheckBox Check(string label, bool value) => new() { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };

    /// <summary>Buttons side by side with the standard 8 px gap.</summary>
    internal static StackPanel ButtonRow(params UIElement[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var button in buttons)
            row.Children.Add(button);
        return row;
    }

    /// <summary>A secondary button (the default look of every button).</summary>
    internal static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    internal static Button Button(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Alert(Window.GetWindow(button), ex.Message, "Alondra1");
            }
        };
        return button;
    }

    /// <summary>The window's main action (blue). Not a default button: Enter in a field must not send mail or save by surprise.</summary>
    internal static Button Primary(string label, Action action)
    {
        var button = Button(label, action);
        button.Style = (Style)Application.Current.FindResource("PrimaryButton");
        button.Margin = new Thickness(0);
        return button;
    }

    internal static Button Primary(string label, Func<Task> action)
    {
        var button = Button(label, action);
        button.Style = (Style)Application.Current.FindResource("PrimaryButton");
        button.Margin = new Thickness(0);
        return button;
    }

    internal static Button Danger(string label, Action action)
    {
        var button = Button(label, action);
        button.Style = (Style)Application.Current.FindResource("DangerButton");
        return button;
    }

    internal static Button Cancel(Window window)
    {
        var button = Button("Cancel", window.Close);
        button.IsCancel = true;
        return button;
    }
}
