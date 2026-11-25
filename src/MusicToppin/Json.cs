using System.Globalization;
using System.Text;

namespace MusicToppin;

internal enum JsonKind
{
    Null,
    Bool,
    Number,
    String,
    Array,
    Object
}

/// <summary>
/// 极简只读 JSON。B 站接口和弹幕都是 JSON，但项目不想为几个字段引入
/// System.Web.Extensions 或 Newtonsoft，所以自带一个约 250 行的解析器。
/// 只解析、不序列化；对象 → Dictionary，数组 → List，数字统一存 double。
/// </summary>
internal sealed class JsonValue
{
    public JsonKind Kind;
    public bool BoolValue;
    public double NumberValue;
    public string StringValue;
    public List<JsonValue> Items;
    public Dictionary<string, JsonValue> Members;

    public static readonly JsonValue Null = new JsonValue { Kind = JsonKind.Null };

    /// <summary>对象成员；不存在返回 null（方便一路 ?. 下去）。</summary>
    public JsonValue this[string key]
    {
        get
        {
            JsonValue value;
            if (Members != null && Members.TryGetValue(key, out value)) return value;
            return null;
        }
    }

    /// <summary>数组元素；越界返回 null。</summary>
    public JsonValue this[int index]
    {
        get
        {
            if (Items == null || index < 0 || index >= Items.Count) return null;
            return Items[index];
        }
    }

    public bool IsNull
    {
        get { return Kind == JsonKind.Null; }
    }

    public int Count
    {
        get
        {
            if (Items != null) return Items.Count;
            if (Members != null) return Members.Count;
            return 0;
        }
    }

    public string AsString
    {
        get
        {
            if (Kind == JsonKind.String) return StringValue;
            if (Kind == JsonKind.Number) return NumberValue.ToString(CultureInfo.InvariantCulture);
            return null;
        }
    }

    public long AsLong
    {
        get
        {
            if (Kind == JsonKind.Number) return (long)NumberValue;
            if (Kind == JsonKind.String)
            {
                long parsed;
                return long.TryParse(StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
            }
            return 0;
        }
    }

    public int AsInt
    {
        get { return (int)AsLong; }
    }

    public double AsDouble
    {
        get
        {
            if (Kind == JsonKind.Number) return NumberValue;
            if (Kind == JsonKind.String)
            {
                double parsed;
                return double.TryParse(StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
            }
            return 0;
        }
    }

    public bool AsBool
    {
        get
        {
            if (Kind == JsonKind.Bool) return BoolValue;
            if (Kind == JsonKind.Number) return NumberValue != 0;
            return false;
        }
    }
}

internal static class Json
{
    /// <summary>解析失败抛 FormatException（带位置），调用方按"这条数据坏了"处理即可。</summary>
    public static JsonValue Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var reader = new Reader(text);
        reader.SkipWhitespace();
        var value = reader.ReadValue();
        return value;
    }

    /// <summary>把字符串转成 JSON 字面量（用于拼极少量的请求体）。</summary>
    public static string Escape(string value)
    {
        if (value == null) return "null";

        var builder = new StringBuilder(value.Length + 8);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }

    private sealed class Reader
    {
        private readonly string _text;
        private int _position;

        public Reader(string text)
        {
            _text = text;
        }

        public void SkipWhitespace()
        {
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _position++;
                else break;
            }
        }

        public JsonValue ReadValue()
        {
            SkipWhitespace();
            if (_position >= _text.Length) throw Error("内容意外结束");

            var c = _text[_position];
            switch (c)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return new JsonValue { Kind = JsonKind.String, StringValue = ReadString() };
                case 't': Expect("true"); return new JsonValue { Kind = JsonKind.Bool, BoolValue = true };
                case 'f': Expect("false"); return new JsonValue { Kind = JsonKind.Bool, BoolValue = false };
                case 'n': Expect("null"); return new JsonValue { Kind = JsonKind.Null };
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                    throw Error("无法识别的字符 '" + c + "'");
            }
        }

        private JsonValue ReadObject()
        {
            var value = new JsonValue
            {
                Kind = JsonKind.Object,
                Members = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            };

            _position++;   // {
            SkipWhitespace();
            if (Peek() == '}') { _position++; return value; }

            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"') throw Error("对象的键必须是字符串");

                var key = ReadString();
                SkipWhitespace();
                if (Peek() != ':') throw Error("对象缺少冒号");
                _position++;

                value.Members[key] = ReadValue();

                SkipWhitespace();
                var c = Peek();
                if (c == ',') { _position++; continue; }
                if (c == '}') { _position++; return value; }
                throw Error("对象缺少逗号或右花括号");
            }
        }

        private JsonValue ReadArray()
        {
            var value = new JsonValue { Kind = JsonKind.Array, Items = new List<JsonValue>() };

            _position++;   // [
            SkipWhitespace();
            if (Peek() == ']') { _position++; return value; }

            while (true)
            {
                value.Items.Add(ReadValue());

                SkipWhitespace();
                var c = Peek();
                if (c == ',') { _position++; continue; }
                if (c == ']') { _position++; return value; }
                throw Error("数组缺少逗号或右方括号");
            }
        }

        private string ReadString()
        {
            _position++;   // 开头的引号

            var builder = new StringBuilder();
            while (true)
            {
                if (_position >= _text.Length) throw Error("字符串没有结束");

                var c = _text[_position++];
                if (c == '"') return builder.ToString();

                if (c != '\\') { builder.Append(c); continue; }

                if (_position >= _text.Length) throw Error("转义符没有内容");
                var escape = _text[_position++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (_position + 4 > _text.Length) throw Error("\\u 转义不完整");
                        int code;
                        if (!int.TryParse(_text.Substring(_position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            throw Error("\\u 转义不是合法的十六进制");
                        }
                        builder.Append((char)code);
                        _position += 4;
                        break;
                    default:
                        throw Error("不支持的转义 \\" + escape);
                }
            }
        }

        private JsonValue ReadNumber()
        {
            var start = _position;
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') _position++;
                else break;
            }

            var text = _text.Substring(start, _position - start);
            double number;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                throw Error("不合法的数字 '" + text + "'");
            }
            return new JsonValue { Kind = JsonKind.Number, NumberValue = number };
        }

        private void Expect(string literal)
        {
            if (_position + literal.Length > _text.Length ||
                string.CompareOrdinal(_text, _position, literal, 0, literal.Length) != 0)
            {
                throw Error("期望 '" + literal + "'");
            }
            _position += literal.Length;
        }

        private char Peek()
        {
            return _position < _text.Length ? _text[_position] : '\0';
        }

        private FormatException Error(string message)
        {
            return new FormatException("JSON 解析失败（位置 " + _position + "）：" + message);
        }
    }
}
