using static DeskMaster.App.Localization.Loc;
using DeskMaster.Core.Drives;
using DeskMaster.Core.Formatting;

namespace DeskMaster.App.ViewModels;

public sealed class DriveItemViewModel(DriveSummary drive) : ViewModelBase
{
    public DriveSummary Drive { get; } = drive;
    public string Name => Drive.DisplayName;
    public string FreeText => F("{0} free", ByteSize.Format(Drive.FreeBytes));
    public string Caption => F("{0} used of {1}", ByteSize.Format(Drive.UsedBytes), ByteSize.Format(Drive.TotalBytes));
    public double UsedPercent => Drive.UsedFraction * 100;
    public bool IsLow => Drive.IsLow;
    public bool IsSystem => Drive.IsSystem;
    public string Kind => Drive.IsSystem ? T("System drive") : Drive.IsRemovable ? T("Removable") : Drive.Format;
}
