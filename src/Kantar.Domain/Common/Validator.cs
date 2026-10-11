namespace Kantar.Domain.Common;

/// <summary>Collects field errors so a request is rejected once with everything that is wrong with it.</summary>
public sealed class Validator
{
    private readonly List<FieldError> _errors = [];

    public bool IsValid => _errors.Count == 0;

    public void Add(string field, string code, string detail) => _errors.Add(new FieldError(field, code, detail));

    public string Text(string field, string? value, int min, int max, bool multiline = false, bool required = true)
    {
        if (!Common.Text.TryClean(value, multiline, out var cleaned))
        {
            Add(field, "invalid_characters", "Denetim veya görünmez karakterler kullanılamaz.");
            return string.Empty;
        }

        if (cleaned.Length == 0)
        {
            if (required)
            {
                Add(field, "required", "Bu alan zorunludur.");
            }

            return string.Empty;
        }

        if (cleaned.Length < min || cleaned.Length > max)
        {
            Add(field, "length", $"Uzunluk {min}–{max} karakter olmalıdır.");
        }

        return cleaned;
    }

    public string Code(string field, string? value, int min, int max)
    {
        var normalized = Common.Text.NormalizeCode(value ?? string.Empty);
        if (!Common.Text.IsCode(normalized, min, max))
        {
            Add(field, "invalid_code", $"{min}–{max} karakter; yalnız A–Z, 0–9 ve - _ / . kullanılabilir.");
        }

        return normalized;
    }

    public int Range(string field, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            Add(field, "out_of_range", $"Değer {min} ile {max} arasında olmalıdır.");
        }

        return value;
    }

    public decimal Money(string field, decimal value, decimal max = 9_999_999_999.99m)
    {
        if (value < 0 || value > max || decimal.Round(value, 2) != value)
        {
            Add(field, "invalid_amount", "Tutar 0 veya daha büyük, en fazla 2 ondalıklı olmalıdır.");
        }

        return value;
    }

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
        {
            throw Problems.Validation(_errors.ToArray());
        }
    }
}
