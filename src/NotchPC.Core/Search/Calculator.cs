using System.Globalization;

namespace NotchPC.Core.Search;

/// <summary>
/// A small calculator for the notch's search box: + − × ÷ ^ %, brackets, unary minus, decimal comma or dot, "x" and
/// "·" for times, sqrt(), pi. Only answers when the text really is a calculation (at least one operator).
/// </summary>
public static class Calculator
{
    public static bool TryEvaluate(string? text, out double result)
    {
        result = 0;
        var s = (text ?? "").Trim().TrimStart('=').Trim();
        if (s.Length == 0 || !s.Any(c => "+-*/^%x×÷·(".Contains(c, StringComparison.Ordinal)) || !s.Any(char.IsDigit))
        {
            return false;
        }

        var p = new Parser(s);
        try
        {
            var value = p.Expression();
            p.SkipSpaces();
            if (!p.AtEnd || double.IsNaN(value) || double.IsInfinity(value))
            {
                return false;
            }

            result = value;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>"39,98" (up to 10 significant decimals, no trailing zeros, grouped thousands).</summary>
    public static string Format(double value, CultureInfo culture)
    {
        var rounded = Math.Round(value, 10);
        return Math.Abs(rounded) >= 1e15 || (rounded != 0 && Math.Abs(rounded) < 1e-6)
            ? rounded.ToString("G10", culture)
            : rounded.ToString("#,0.##########", culture);
    }

    private sealed class Parser(string text)
    {
        private int _i;

        public bool AtEnd => _i >= text.Length;

        public void SkipSpaces()
        {
            while (_i < text.Length && char.IsWhiteSpace(text[_i]))
            {
                _i++;
            }
        }

        // expression := term (('+' | '-') term)*
        public double Expression()
        {
            var v = Term();
            while (true)
            {
                SkipSpaces();
                if (Take('+'))
                {
                    v += Term();
                }
                else if (Take('-') || Take('−'))
                {
                    v -= Term();
                }
                else
                {
                    return v;
                }
            }
        }

        // term := power (('*' | '/' | 'x' | '×' | '÷' | '·') power)*
        private double Term()
        {
            var v = Power();
            while (true)
            {
                SkipSpaces();
                if (Take('*') || Take('x') || Take('X') || Take('×') || Take('·'))
                {
                    v *= Power();
                }
                else if (Take('/') || Take('÷') || Take(':'))
                {
                    var d = Power();
                    if (d == 0)
                    {
                        throw new FormatException("division by zero");
                    }

                    v /= d;
                }
                else
                {
                    return v;
                }
            }
        }

        // power := unary ('^' power)?
        private double Power()
        {
            var b = Unary();
            SkipSpaces();
            return Take('^') ? Math.Pow(b, Power()) : b;
        }

        private double Unary()
        {
            SkipSpaces();
            if (Take('-') || Take('−'))
            {
                return -Unary();
            }

            if (Take('+'))
            {
                return Unary();
            }

            var v = Primary();
            SkipSpaces();
            while (Take('%'))
            {
                v /= 100;
                SkipSpaces();
            }

            return v;
        }

        private double Primary()
        {
            SkipSpaces();
            if (Take('('))
            {
                var v = Expression();
                SkipSpaces();
                if (!Take(')'))
                {
                    throw new FormatException("missing )");
                }

                return v;
            }

            if (Word("sqrt") || Word("√"))
            {
                var arg = Primary();
                return arg < 0 ? throw new FormatException("sqrt of negative") : Math.Sqrt(arg);
            }

            if (Word("pi") || Word("π"))
            {
                return Math.PI;
            }

            var start = _i;
            while (_i < text.Length && (char.IsDigit(text[_i]) || text[_i] is '.' or ',' || (text[_i] == ' ' && NextIsDigitGroup())))
            {
                _i++;
            }

            if (start == _i)
            {
                throw new FormatException("number expected");
            }

            var number = text[start.._i].Replace(" ", "", StringComparison.Ordinal).Replace(',', '.');
            return double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw new FormatException("bad number");
        }

        /// <summary>"1 000 000" (space as a thousands separator, Polish style) stays one number.</summary>
        private bool NextIsDigitGroup() =>
            _i + 3 < text.Length + 1 && _i + 3 <= text.Length && text.AsSpan(_i + 1).Length >= 3
            && char.IsDigit(text[_i + 1]) && char.IsDigit(text[_i + 2]) && char.IsDigit(text[_i + 3])
            && (_i + 4 >= text.Length || !char.IsDigit(text[_i + 4]));

        private bool Take(char c)
        {
            if (_i < text.Length && text[_i] == c)
            {
                _i++;
                return true;
            }

            return false;
        }

        private bool Word(string w)
        {
            if (string.Compare(text, _i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                _i += w.Length;
                return true;
            }

            return false;
        }
    }
}
