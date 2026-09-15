namespace MechMaker.Core.Parametric;

/// <summary>
/// A tiny arithmetic evaluator for parametric catalog geometry: number literals,
/// parameter references, unary minus, + - * / and parentheses — e.g.
/// "length_m/2", "module_mm*teeth/2000", "(length_m - 0.02)/2".
/// No functions, no state, deterministic: geometry must not be clever.
/// </summary>
public static class ParamEval
{
    public static double Evaluate(string expression, IReadOnlyDictionary<string, double> parameters)
        => new Parser(expression, parameters).Evaluate();

    private sealed class Parser
    {
        private readonly string _text;
        private readonly IReadOnlyDictionary<string, double> _parameters;
        private int _pos;

        public Parser(string text, IReadOnlyDictionary<string, double> parameters)
        {
            _text = text;
            _parameters = parameters;
        }

        public double Evaluate()
        {
            var value = ParseExpression();
            SkipWhitespace();
            if (_pos < _text.Length)
                throw new ParamEvalException($"Unexpected '{_text[_pos]}' at position {_pos} in '{_text}'.");
            return value;
        }

        private double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] is not ('+' or '-'))
                    return value;
                var op = _text[_pos];
                _pos++;
                var right = ParseTerm();
                value = op == '+' ? value + right : value - right;
            }
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] is '*' or '/')
                {
                    var op = _text[_pos++];
                    var right = ParseFactor();
                    if (op == '/') value /= right;
                    else value *= right;
                }
                else return value;
            }
        }

        private double ParseFactor()
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
                throw new ParamEvalException($"Unexpected end of expression '{_text}'.");
            if (_text[_pos] is '+' or '-')
            {
                var sign = _text[_pos] == '-' ? -1.0 : 1.0;
                _pos++;
                return sign * ParseFactor();
            }
            if (_text[_pos] == '(')
            {
                _pos++;
                var value = ParseExpression();
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != ')')
                    throw new ParamEvalException($"Missing ')' in '{_text}'.");
                _pos++;
                return value;
            }
            if (char.IsDigit(_text[_pos]) || _text[_pos] == '.')
                return ParseNumber();
            if (char.IsLetter(_text[_pos]) || _text[_pos] == '_')
                return ParseParameter();
            throw new ParamEvalException($"Unexpected character '{_text[_pos]}' at position {_pos} in '{_text}'.");
        }

        private double ParseNumber()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] is '.' or 'e' or 'E' or '+' or '-'))
            {
                // '+'/'-' belong to this number only right after e/E (scientific notation).
                if (_pos > start && _text[_pos] is '-' or '+' && _text[_pos - 1] is not 'e' and not 'E')
                    break;
                if (_pos > start && _text[_pos] is 'e' or 'E' && !char.IsDigit(_text[_pos - 1]))
                    break;
                _pos++;
            }
            var token = _text[start.._pos];
            if (!double.TryParse(token, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                throw new ParamEvalException($"'{token}' is not a number in '{_text}'.");
            return value;
        }

        private double ParseParameter()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
                _pos++;
            var name = _text[start.._pos];
            if (!_parameters.TryGetValue(name, out var value))
                throw new ParamEvalException($"Unknown parameter '{name}' in '{_text}'.");
            return value;
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                _pos++;
        }
    }
}

public sealed class ParamEvalException(string message) : Exception(message);
