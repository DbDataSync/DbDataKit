#r "nuget: Newtonsoft.Json, 13.0.3"
#r "nuget: Humanizer.Core, 2.14.1"
using Newtonsoft.Json;
using Humanizer;

var json = JsonConvert.SerializeObject(new Dictionary<string, int> { ["n"] = 3 });
$"{json} / {3.Minutes().Humanize()} / {"scripting_with_nuget".Humanize()}"
