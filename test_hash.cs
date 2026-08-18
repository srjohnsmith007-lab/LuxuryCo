using System;
using System.Security.Cryptography;
using System.Text;

class Program
{
    static void Main()
    {
        string reference = "test_ref";
        long amountInCents = 1000000;
        string integritySecret = "test_integrity_gj7eMblroNF7lDTdEnK0YTjCsP5iR8SB";
        
        var rawSignature = $"{reference}{amountInCents}COP{integritySecret}";
        var signatureBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawSignature));
        var signature = Convert.ToHexString(signatureBytes).ToLower();
        
        Console.WriteLine($"Raw: {rawSignature}");
        Console.WriteLine($"Signature: {signature}");
    }
}
