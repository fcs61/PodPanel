using System;
using System.Windows;
using System.Windows.Controls;

namespace PodPanel;

/// <summary>
/// Çocuklarını tek satıra dizer ve genişliği metinlerinin doğal genişliğiyle orantılı paylaştırır. Dinleme modu
/// düğmeleri için: adların uzunluğu dile göre çok değişir ("Şeffaf" / "Geräuschunterdrückung"), sabit oranlı
/// sütunlar bir dilde sığan metni başka dilde kırpar.
/// </summary>
public sealed class ShareRow : Panel
{
    /// <summary>Hiçbir düğme satırın bu oranından dar olmaz (tıklanabilir kalsın).</summary>
    private const double MinShare = 0.22;

    private double[] _natural = Array.Empty<double>();

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = InternalChildren.Count;
        if (_natural.Length != count) _natural = new double[count];
        double total = 0, height = 0;
        for (int i = 0; i < count; i++)
        {
            UIElement child = InternalChildren[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            _natural[i] = child.DesiredSize.Width;
            total += _natural[i];
        }
        if (double.IsInfinity(availableSize.Width)) availableSize.Width = total;

        double[] widths = Widths(availableSize.Width);
        for (int i = 0; i < count; i++)
        {
            UIElement child = InternalChildren[i];
            // Paylaşılan genişlikle yeniden ölç: sığmayan metin üç noktayla kırpılır.
            child.Measure(new Size(widths[i], availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double[] widths = Widths(finalSize.Width);
        double x = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i];
        }
        return finalSize;
    }

    private double[] Widths(double available)
    {
        int count = _natural.Length;
        var widths = new double[count];
        if (count == 0 || available <= 0) return widths;
        double total = 0;
        foreach (double width in _natural) total += width;
        if (total <= 0)
        {
            for (int i = 0; i < count; i++) widths[i] = available / count;
            return widths;
        }
        // Doğal genişlikle orantılı; alt sınırın altında kalanlar sınıra çekilir, fark diğerlerinden düşülür.
        double floor = Math.Min(MinShare, 1.0 / count) * available;
        double spare = available, flexible = 0;
        for (int i = 0; i < count; i++)
        {
            widths[i] = _natural[i] / total * available;
            if (widths[i] < floor)
            {
                widths[i] = floor;
                spare -= floor;
            }
            else
            {
                flexible += widths[i];
            }
        }
        if (flexible > 0)
        {
            for (int i = 0; i < count; i++)
            {
                if (widths[i] > floor) widths[i] = widths[i] / flexible * spare;
            }
        }
        return widths;
    }
}
