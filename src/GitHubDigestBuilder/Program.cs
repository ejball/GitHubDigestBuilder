using System;
using System.Collections.Generic;

namespace GitHubDigestBuilder
{
	public static class Program
	{
		public static void Run(IReadOnlyList<string> args)
		{
			if (args.Count != 0)
				throw new ApplicationException("No arguments supported yet.");
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
