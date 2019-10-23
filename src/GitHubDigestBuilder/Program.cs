using System;
using System.Collections.Generic;
using System.IO;
using HandlebarsDotNet;

namespace GitHubDigestBuilder
{
	public static class Program
	{
		public static void Run(IReadOnlyList<string> args)
		{
			if (args.Count != 0)
				throw new ApplicationException("No arguments supported yet.");

			string templateText;
			using (var reader = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream("GitHubDigestBuilder.template.html") ?? throw new InvalidOperationException()))
				templateText = reader.ReadToEnd();

			var template = Handlebars.Compile(templateText);

			Console.WriteLine(template(new RootData { Test = "This & That" }));
		}

		public static int Main(string[] args)
		{
			try
			{
				Run(args);
				return 0;
			}
			catch (ApplicationException exception)
			{
				Console.Error.WriteLine(exception.Message);
				return 1;
			}
			catch (Exception exception)
			{
				Console.Error.WriteLine(exception);
				return 1;
			}
		}
	}
}
