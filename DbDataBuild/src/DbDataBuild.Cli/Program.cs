// Output is UTF-8 everywhere. On Windows the console default is an OEM code page, which turns an accent in a sample row, a model name or a JSON document into a question mark
// (and a JSON document piped to a file would not be valid UTF-8). The JSON documents and the rendered files are UTF-8 by design, so the console must be too.
var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = utf8;
Console.InputEncoding = utf8;
Console.Out.NewLine = "\n";      // like everything the tool writes: the same bytes on every platform, so a captured run diffs cleanly
Console.Error.NewLine = "\n";
return DbDataBuild.Cli.CliApp.Run(args, Console.Out, Console.Error, Console.In, interactive: !Console.IsInputRedirected);
