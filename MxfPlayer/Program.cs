using LibVLCSharp.Shared;
using MxfPlayer;
using System;
using System.Windows.Forms;

namespace MxfPlayer
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Core.Initialize();
            Application.Run(new MainForm());
        }
    }
}