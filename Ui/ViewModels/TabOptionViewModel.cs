namespace QsrPriceBenchmarks.Ui.ViewModels;

/// <summary>
/// One TG menu tab shown as a checkbox in the Run Pipeline tab when a full run
/// is selected. <see cref="IsSelected"/> is two-way bound so the user can
/// exclude tabs from the scrape; unticked tabs are dropped from the run.
/// </summary>
public sealed class TabOptionViewModel(string name) : ViewModelBase
{
    private bool _isSelected = true;

    public string Name { get; } = name;

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
