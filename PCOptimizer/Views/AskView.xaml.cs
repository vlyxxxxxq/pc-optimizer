using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class AskView : UserControl
{
    readonly AiAssistant _ai = new();
    Func<string> _findings = () => "";
    CancellationTokenSource? _cancel;

    public AskView()
    {
        InitializeComponent();
        RenderKey();
    }

    /// <param name="findings">PC Optimizer's own health findings, added to each snapshot.</param>
    public void Attach(Func<string> findings) => _findings = findings;

    /// <summary>Opens the page with a question already asked (from the command palette).</summary>
    public async void AskNow(string question)
    {
        QuestionBox.Text = question;
        await AskAsync();
    }

    void RenderKey()
    {
        bool has = AiAssistant.HasKey;
        KeyCard.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        KeyStatus.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (!key.StartsWith("sk-ant-", StringComparison.Ordinal))
        {
            MessageBox.Show("That doesn't look like an Anthropic API key (they start with sk-ant-).", "Ask AI");
            return;
        }
        AiAssistant.SaveKey(key);
        KeyBox.Clear();
        RenderKey();
    }

    void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        AiAssistant.SaveKey(null);
        RenderKey();
    }

    void GetKey_Click(object sender, RoutedEventArgs e) => Shell.Open("https://console.anthropic.com/settings/keys");

    void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _cancel?.Cancel();
        _ai.NewConversation();
        Chat.Children.Clear();
        Suggestions.Visibility = Visibility.Visible;
    }

    async void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        QuestionBox.Text = (string)((Button)sender).Content;
        await AskAsync();
    }

    async void Ask_Click(object sender, RoutedEventArgs e) => await AskAsync();

    async void Question_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; await AskAsync(); }
    }

    void Question_Changed(object sender, TextChangedEventArgs e) =>
        QuestionHint.Visibility = QuestionBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    async Task AskAsync()
    {
        var question = QuestionBox.Text.Trim();
        if (question.Length == 0 || !AskButton.IsEnabled) return;
        if (!AiAssistant.HasKey) { MessageBox.Show("Paste your Anthropic API key above first.", "Ask AI"); return; }

        QuestionBox.Clear();
        Suggestions.Visibility = Visibility.Collapsed;
        AddBubble(question, mine: true);
        var pending = AddBubble("Looking at your PC…", mine: false);
        AskButton.IsEnabled = false;
        _cancel = new CancellationTokenSource();
        try
        {
            var findings = _findings();
            var snapshot = await Task.Run(() => AiAssistant.Snapshot(findings));
            ((TextBlock)pending.Child).Text = "Thinking…";
            var answer = await _ai.AskAsync(question, snapshot, _cancel.Token);
            pending.Child = Render(answer);
        }
        catch (OperationCanceledException) { Chat.Children.Remove(pending); }
        catch (Exception ex)
        {
            ((TextBlock)pending.Child).Text = ex is InvalidOperationException ? ex.Message : $"Something went wrong: {ex.Message}";
            ((TextBlock)pending.Child).Foreground = (Brush)FindResource("WarnBrush");
            RenderKey();
        }
        finally
        {
            AskButton.IsEnabled = true;
            ChatScroll.ScrollToEnd();
        }
    }

    Border AddBubble(string text, bool mine)
    {
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = mine ? new Thickness(120, 0, 0, 10) : new Thickness(0, 0, 80, 10),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = mine ? new SolidColorBrush(Color.FromRgb(0x2A, 0x4C, 0x9E)) : (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("CardBorderBrush"),
            BorderThickness = new Thickness(mine ? 0 : 1),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = mine ? Brushes.White : (Brush)FindResource("MutedBrush") },
        };
        Chat.Children.Add(bubble);
        ChatScroll.ScrollToEnd();
        return bubble;
    }

    /// <summary>Just enough Markdown for chat answers: headings, bullets, numbered steps, **bold** and `code`.</summary>
    static TextBlock Render(string markdown)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        bool first = true;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            if (!first) tb.Inlines.Add(new LineBreak());
            first = false;
            var line = raw.TrimEnd();
            if (Regex.Match(line, @"^#{1,6}\s+(.*)") is { Success: true } h)
            {
                tb.Inlines.Add(new Run(h.Groups[1].Value) { FontWeight = FontWeights.SemiBold, FontSize = 14 });
                continue;
            }
            if (Regex.Match(line, @"^(\s*)[-*]\s+(.*)") is { Success: true } b) line = b.Groups[1].Value + "  •  " + b.Groups[2].Value;
            // **bold** and `code` spans
            foreach (var part in Regex.Split(line, @"(\*\*[^*]+\*\*|`[^`]+`)"))
            {
                if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                    tb.Inlines.Add(new Run(part[2..^2]) { FontWeight = FontWeights.SemiBold });
                else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
                    tb.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x90, 0xB0)) });
                else if (part.Length > 0)
                    tb.Inlines.Add(new Run(part));
            }
        }
        return tb;
    }
}
