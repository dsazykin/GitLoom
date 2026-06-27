using System;
using System.Reflection;
using LibGit2Sharp;

class Program
{
    static void Main()
    {
        var type = typeof(MergeOptions);
        foreach (var prop in type.GetProperties())
        {
            Console.WriteLine($""{prop.PropertyType.Name} {prop.Name}"");
        }
    }
}
