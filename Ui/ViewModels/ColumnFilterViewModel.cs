using System.Collections.ObjectModel;

namespace QsrPriceBenchmarks.Ui.ViewModels;

/// <summary>
/// One column's Excel-style filter (maintainer, 2026-10-29): a checkbox per value, "(Select all)", and a funnel in
/// the header that shows while anything is filtered out. <see cref="Changed"/> fires whenever a tick changes.
/// </summary>
public sealed class ColumnFilterViewModel : ViewModelBase
{
    private bool _settingAll;

    public ColumnFilterViewModel(string title, IEnumerable<string> values)
    {
        Title = title;
        foreach (string value in values)
        {
            FilterOptionViewModel option = new(value);
            option.PropertyChanged += (_, _) => OnOptionChanged();
            Options.Add(option);
        }
    }

    /// <summary>The column's header text.</summary>
    public string Title { get; }

    public ObservableCollection<FilterOptionViewModel> Options { get; } = [];

    /// <summary>Fires whenever what the filter lets through changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// "(Select all)": true when every value is ticked, false when none is, null in between. Setting it ticks or
    /// unticks every value.
    /// </summary>
    public bool? AllSelected
    {
        get
        {
            int ticked = Options.Count(o => o.IsSelected);
            return ticked == Options.Count ? true : ticked == 0 ? false : null;
        }

        set
        {
            bool tick = value ?? true;
            _settingAll = true;
            foreach (FilterOptionViewModel option in Options)
            {
                option.IsSelected = tick;
            }
            _settingAll = false;
            OnOptionChanged();
        }
    }

    /// <summary>True while the filter hides something: the header's funnel is highlighted then.</summary>
    public bool IsActive
    {
        get
        {
            return Options.Any(o => !o.IsSelected);
        }
    }

    /// <summary>Whether rows with <paramref name="value"/> in this column are shown.</summary>
    public bool Allows(string value)
    {
        FilterOptionViewModel? option = Options.FirstOrDefault(o => o.Value == value);
        return option is null || option.IsSelected; // a value the list does not know is never hidden
    }

    private void OnOptionChanged()
    {
        if (_settingAll)
        {
            return; // one notification for the whole "(Select all)" change
        }
        Notify(nameof(AllSelected));
        Notify(nameof(IsActive));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One value in a <see cref="ColumnFilterViewModel"/>, ticked when its rows are shown.</summary>
public sealed class FilterOptionViewModel(string value) : ViewModelBase
{
    private bool _isSelected = true;

    public string Value { get; } = value;

    public bool IsSelected
    {
        get
        {
            return _isSelected;
        }

        set
        {
            Set(ref _isSelected, value);
        }
    }
}
