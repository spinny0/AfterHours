using System;
using System.Windows;
using System.Windows.Markup;

namespace AfterHours;

internal sealed partial class Dashboard
{
    private Action? refreshLimits;

    public void UpdateLimitUsage()
    {
        if (IsVisible) refreshLimits?.Invoke();
    }

    private static readonly Style ToggleStyle = (Style)XamlReader.Parse("""
        <Style TargetType="ToggleButton" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Setter Property="FocusVisualStyle" Value="{DynamicResource DefaultControlFocusVisualStyle}"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ToggleButton">
                <Grid Width="82" Height="30" Background="Transparent">
                  <TextBlock x:Name="state" Text="Off" VerticalAlignment="Center"
                             Foreground="{DynamicResource TextFillColorSecondaryBrush}"/>
                  <Border x:Name="track" Width="40" Height="20" HorizontalAlignment="Right" CornerRadius="10"
                          Background="{DynamicResource ControlAltFillColorSecondaryBrush}"
                          BorderThickness="1" BorderBrush="{DynamicResource ControlStrongStrokeColorDefaultBrush}">
                    <Ellipse x:Name="thumb" Width="10" Height="10" HorizontalAlignment="Left"
                             Margin="4,0" Fill="{DynamicResource TextFillColorPrimaryBrush}"/>
                  </Border>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="state" Property="Text" Value="On"/>
                    <Setter TargetName="track" Property="Background" Value="{DynamicResource AccentFillColorDefaultBrush}"/>
                    <Setter TargetName="track" Property="BorderBrush" Value="{DynamicResource AccentFillColorDefaultBrush}"/>
                    <Setter TargetName="thumb" Property="HorizontalAlignment" Value="Right"/>
                    <Setter TargetName="thumb" Property="Fill" Value="{DynamicResource TextOnAccentFillColorPrimaryBrush}"/>
                  </Trigger>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="track" Property="Opacity" Value="0.82"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);
}
