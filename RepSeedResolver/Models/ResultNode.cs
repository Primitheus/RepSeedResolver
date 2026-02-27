using Newtonsoft.Json.Linq;

namespace RepSeedResolver;

public class ResultNode
{
    public string Header { get; }
    public bool IsTopLevel { get; init; }
    public bool IsPlaceholder { get; init; }

    private readonly JToken? _token;
    private IReadOnlyList<ResultNode>? _children;

    public ResultNode(string header, JToken? token = null)
    {
        Header = header;
        _token = token;
    }

    public IReadOnlyList<ResultNode> Children
    {
        get
        {
            if (_children != null) return _children;

            var list = new List<ResultNode>();

            switch (_token)
            {
                case JObject obj:
                    foreach (var p in obj.Properties())
                    {
                        if (p.Value is JObject or JArray)
                            list.Add(new ResultNode(p.Name, p.Value));
                        else
                            list.Add(new ResultNode($"{p.Name}: {p.Value}"));
                    }
                    break;

                case JArray arr:
                    for (int i = 0; i < arr.Count; i++)
                    {
                        var item = arr[i];
                        var h = item is JObject io
                            ? $"[{i}] {io["name"] ?? io["h"] ?? ""}"
                            : $"[{i}] {item}";
                        list.Add(new ResultNode(h, item is JObject or JArray ? item : null));
                    }
                    break;
            }

            if (list.Count == 0 && _token is JObject or JArray)
                list.Add(new ResultNode("(empty)") { IsPlaceholder = true });

            _children = list;
            return _children;
        }
    }
}
