namespace Listen_N
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "replay", StringComparison.OrdinalIgnoreCase))
            {
                int exitCode = RtReplayRunner.RunCli(args.Skip(1).ToArray());
                Environment.Exit(exitCode);
                return;
            }

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ListenN());

        }
    }
}
