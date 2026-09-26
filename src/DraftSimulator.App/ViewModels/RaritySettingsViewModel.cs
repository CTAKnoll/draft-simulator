using CommunityToolkit.Mvvm.ComponentModel;
using DraftSimulator.Core;

namespace DraftSimulator.App.ViewModels;

public sealed partial class RaritySettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private int _minimum;

    [ObservableProperty]
    private int _maximum;

    [ObservableProperty]
    private double _weight;

    public RaritySettingsViewModel(Rarity rarity, int maximum)
    {
        Rarity = rarity;
        DisplayName = rarity switch
        {
            Rarity.SuperRare => "Super Rare",
            Rarity.UltraRare => "Ultra Rare",
            Rarity.MythicRare => "Mythic Rare",
            Rarity.Special => "Special",
            Rarity.Bonus => "Bonus",
            _ => rarity.ToString(),
        };
        _maximum = maximum;
        _weight = 1;
    }

    public Rarity Rarity { get; }
    public string DisplayName { get; }
    public bool IsWeightEnabled => Minimum != Maximum;
    public bool IsMaximumEnabled => Weight != 0;

    partial void OnMinimumChanged(int value)
    {
        OnPropertyChanged(nameof(IsWeightEnabled));
    }

    partial void OnMaximumChanged(int value)
    {
        OnPropertyChanged(nameof(IsWeightEnabled));
    }

    partial void OnWeightChanged(double value)
    {
        OnPropertyChanged(nameof(IsMaximumEnabled));
    }
}
