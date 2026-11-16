using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>
/// Past transcriptions: searchable, each copyable and deletable.
/// </summary>
/// <remarks>
/// Rows show which dictionary corrections fired. Without that the dictionary is invisible and
/// there is no way to tell a rule that works from one that never matches.
/// </remarks>
public sealed class TranscriptionsView : UserControl
{
    private readonly TranscriptStore _store;
    private readonly TextBox _search;
    private readonly StackPanel _list;
    private readonly Silkscreen _count;

    /// <summary>Days the user has opened or closed. Missing means the default.</summary>
    private readonly Dictionary<DateTime, bool> _dayOpen = [];

    /// <summary>Builds the view over <paramref name="store"/>.</summary>
    public TranscriptionsView(TranscriptStore store)
    {
        _store = store;

        _search = Panels.SearchBox("Search transcriptions");
        _search.TextChanged += (_, _) => Refresh();

        _list = new StackPanel { Spacing = Tokens.Space.Snug, Margin = new Thickness(Tokens.Space.Base) };
        _count = new Silkscreen { Foreground = new SolidColorBrush(Tokens.Colors.InkOnDeck, 0.5) };

        var clear = Panels.DeckButton("DELETE ALL");
        clear.Click += (_, _) => { _store.Clear(); Refresh(); };

        Content = new DockPanel
        {
            Children =
            {
                Panels.Docked(Panels.SearchRow(_search), Dock.Top),
                Panels.Docked(Panels.Footer(_count, clear), Dock.Bottom),
                new ScrollViewer { Content = _list },
            },
        };

        // Completed is raised off the UI thread, after the history file has been appended.
        _store.Changed += (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess()) Refresh();
            else Dispatcher.UIThread.Post(Refresh);
        };
        Refresh();
    }

    private void Refresh()
    {
        var records = _store.Search(_search.Text ?? string.Empty);

        _list.Children.Clear();
        _count.Text = $"{_store.Records.Count} RECORDING{(_store.Records.Count == 1 ? "" : "S")}";

        if (records.Count == 0)
        {
            _list.Children.Add(Panels.EmptyState(
                _store.Records.Count == 0 ? "NO RECORDINGS" : "NO MATCHES",
                _store.Records.Count == 0 ? "Press the key to start, and press it again to stop." : "Try a different search."));
            return;
        }

        // A search has to show its hits. Folding them back is for the unfiltered list.
        var searching = !string.IsNullOrWhiteSpace(_search.Text);
        foreach (var (day, rows) in GroupByDay(records))
        {
            var open = searching || IsDayOpen(day);
            _list.Children.Add(BuildDateDivider(day, rows.Count, open));

            if (!open) continue;

            foreach (var record in rows) _list.Children.Add(BuildRow(record));
        }
    }

    /// <summary>True when <paramref name="day"/> is expanded. Today starts open. Older days start closed.</summary>
    private bool IsDayOpen(DateTime day) =>
        _dayOpen.TryGetValue(day, out var open) ? open : day == DateTime.Today;

    private void ToggleDay(DateTime day)
    {
        _dayOpen[day] = !IsDayOpen(day);
        Refresh();
    }

    private static List<(DateTime Day, List<TranscriptRecord> Rows)> GroupByDay(IReadOnlyList<TranscriptRecord> records)
    {
        var groups = new List<(DateTime, List<TranscriptRecord>)>();
        foreach (var record in records)
        {
            var day = record.At.ToLocalTime().Date;
            if (groups.Count == 0 || groups[^1].Item1 != day) groups.Add((day, []));
            groups[^1].Item2.Add(record);
        }

        return groups;
    }

    private Border BuildRow(TranscriptRecord record)
    {
        var copy = Panels.DeckButton("COPY");
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(record.Text).ConfigureAwait(true);

            copy.Content = "COPIED";
            await Task.Delay(TimeSpan.FromSeconds(1.4)).ConfigureAwait(true);
            copy.Content = "COPY";
        };

        var delete = Panels.DeckButton("DELETE");
        delete.Click += (_, _) => _store.Remove(record.Id);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children =
            {
                new Silkscreen
                {
                    Text = record.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                    Foreground = new SolidColorBrush(Tokens.Colors.InkOnDeck, 0.55),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = record.ProcessingSeconds.ToString("0.00", CultureInfo.CurrentCulture) + "s",
                    FontFamily = Tokens.Fonts.Mono,
                    FontSize = Tokens.Fonts.Caption,
                    Foreground = new SolidColorBrush(Tokens.Colors.InkOnDeck, 0.45),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Tight,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { copy, delete },
        };

        var body = new StackPanel
        {
            Spacing = Tokens.Space.Snug,
            Children =
            {
                new Grid { Children = { header, actions } },
                new TextBlock
                {
                    Text = record.Text,
                    FontFamily = Tokens.Fonts.Grotesque,
                    FontSize = Tokens.Fonts.Body,
                    Foreground = Tokens.Brushes.InkOnDeck,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        if (record.Corrections is { Count: > 0 } corrections)
        {
            body.Children.Add(BuildCorrectionBadges(corrections));
        }

        return Panels.DeckCard(body);
    }

    /// <summary>A day heading. Clicking it folds or opens that day's recordings.</summary>
    private Button BuildDateDivider(DateTime day, int count, bool open)
    {
        var today = DateTime.Today;
        var label = day == today
            ? "Today"
            : day == today.AddDays(-1)
                ? "Yesterday"
                : day.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

        var ink = new SolidColorBrush(Tokens.Colors.InkOnDeck, 0.55);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"),
            ColumnSpacing = Tokens.Space.Snug,
        };

        grid.Children.Add(Mark(open ? "–" : "+", ink));
        grid.Children.Add(Mark(label, ink));
        Grid.SetColumn(grid.Children[1], 1);

        var tally = Mark(count.ToString(CultureInfo.InvariantCulture), new SolidColorBrush(Tokens.Colors.InkOnDeck, 0.4));
        Grid.SetColumn(tally, 2);
        grid.Children.Add(tally);

        var rule = new Border
        {
            Height = Tokens.Border.Hairline,
            Background = new SolidColorBrush(Tokens.Colors.InkOnDeck, 0.28),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rule, 3);
        grid.Children.Add(rule);

        var button = new Button
        {
            Content = grid,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(Tokens.Space.Tight, Tokens.Space.Snug),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) => ToggleDay(day);
        return button;
    }

    private static Silkscreen Mark(string text, IBrush foreground) => new()
    {
        Text = text,
        Foreground = foreground,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Shows that the dictionary fired, and on what.</summary>
    private static WrapPanel BuildCorrectionBadges(IReadOnlyList<Dictionary.AppliedCorrection> corrections)
    {
        var row = new WrapPanel { ItemSpacing = Tokens.Space.Snug, LineSpacing = Tokens.Space.Tight };

        row.Children.Add(new Silkscreen
        {
            Text = "CORRECTED",
            Foreground = new SolidColorBrush(Tokens.Colors.MeterAmber),
            VerticalAlignment = VerticalAlignment.Center,
        });

        foreach (var correction in corrections)
        {
            var label = correction.Count > 1
                ? $"{correction.From} → {correction.To} ×{correction.Count}"
                : $"{correction.From} → {correction.To}";

            row.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Tokens.Colors.MeterAmber, 0.35),
                BorderThickness = new Thickness(Tokens.Border.Hairline),
                CornerRadius = new CornerRadius(Tokens.Radius.Chip),
                Padding = new Thickness(Tokens.Space.Snug, Tokens.Space.Hair),
                Child = new TextBlock
                {
                    Text = label,
                    FontFamily = Tokens.Fonts.Grotesque,
                    FontSize = Tokens.Fonts.Caption,
                    Foreground = Tokens.Brushes.InkOnDeck,
                },
            });
        }

        return row;
    }
}
