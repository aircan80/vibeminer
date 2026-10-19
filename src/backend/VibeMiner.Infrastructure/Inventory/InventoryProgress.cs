using System.Globalization;
namespace VibeMiner.Infrastructure;

internal static class InventoryProgress
{
    public static string Format(string stage, long completed, long total, string unit)
    {
        if (completed < 0 || total < completed) throw new ArgumentOutOfRangeException(nameof(completed));
        if (total == 0) return $"{stage} · 0 {unit}";
        var percent = (long)((decimal)completed * 100 / total);
        return string.Create(CultureInfo.InvariantCulture, $"{stage} · {completed:N0} of {total:N0} {unit} · {percent}%");
    }
}
