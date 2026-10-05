using System.Net;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.UI.Components.Verify;

/// <summary>Port of generateCertificateSvg() — the fallback certificate image when a batch has no uploaded document.</summary>
public static class VerifyCertificateSvg
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string Generate(TrustBatchDto batch)
    {
        var bNo = E(string.IsNullOrEmpty(batch.BatchNo) ? (string.IsNullOrEmpty(batch.Id) ? "BATCH-2026-01" : batch.Id) : batch.BatchNo);
        var mfg = E(string.IsNullOrEmpty(batch.MfgDate) ? "08 Sep 2026" : batch.MfgDate);
        var exp = E(string.IsNullOrEmpty(batch.ExpDate) ? "07 Sep 2028" : batch.ExpDate);
        var lab = E(string.IsNullOrEmpty(batch.LabName) ? "Apex Analytical Labs (NABL Accredited)" : batch.LabName);

        var svg = $"""
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 1100" width="800" height="1100" style="background:#ffffff;font-family:system-ui,-apple-system,sans-serif;">
  <rect x="20" y="20" width="760" height="1060" rx="8" fill="#ffffff" stroke="#0f172a" stroke-width="4"/>
  <rect x="30" y="30" width="740" height="1040" rx="6" fill="#ffffff" stroke="#e2e8f0" stroke-width="2"/>

  <text x="400" y="90" text-anchor="middle" font-size="24" font-weight="900" letter-spacing="4" fill="#0f172a">NURA HERBEX</text>
  <text x="400" y="115" text-anchor="middle" font-size="12" font-weight="600" letter-spacing="2" fill="#64748b">OFFICIAL CERTIFICATE OF ANALYSIS (COA)</text>
  <line x1="60" y1="135" x2="740" y2="135" stroke="#0f172a" stroke-width="2"/>

  <rect x="60" y="155" width="680" height="65" rx="4" fill="#f8fafc" stroke="#e2e8f0" stroke-width="1"/>
  <text x="80" y="180" font-size="11" font-weight="700" fill="#475569">ACCREDITED TESTING LABORATORY:</text>
  <text x="80" y="202" font-size="14" font-weight="800" fill="#0f172a">{lab}</text>
  <text x="720" y="193" text-anchor="end" font-size="11" font-weight="700" fill="#059669">NABL ACCREDITED · FSSAI APPROVED</text>

  <rect x="60" y="235" width="680" height="105" rx="4" fill="#ffffff" stroke="#cbd5e1" stroke-width="1"/>
  <line x1="60" y1="288" x2="740" y2="288" stroke="#e2e8f0" stroke-width="1"/>
  <line x1="400" y1="235" x2="400" y2="340" stroke="#e2e8f0" stroke-width="1"/>

  <text x="80" y="260" font-size="11" fill="#64748b">Product Name:</text>
  <text x="80" y="278" font-size="13" font-weight="700" fill="#0f172a">STAMIX™ Botanical Vitality Formula</text>

  <text x="420" y="260" font-size="11" fill="#64748b">Official Batch Number:</text>
  <text x="420" y="278" font-size="14" font-weight="800" fill="#059669">{bNo}</text>

  <text x="80" y="312" font-size="11" fill="#64748b">Manufacturing Date: <tspan font-weight="700" fill="#0f172a">{mfg}</tspan></text>
  <text x="80" y="330" font-size="11" fill="#64748b">Pack Format: <tspan font-weight="700" fill="#0f172a">300g Clinical Canister</tspan></text>

  <text x="420" y="312" font-size="11" fill="#64748b">Expiry Date: <tspan font-weight="700" fill="#0f172a">{exp}</tspan></text>
  <text x="420" y="330" font-size="11" fill="#64748b">Assay Verification: <tspan font-weight="700" fill="#059669">100% Conforming</tspan></text>

  <text x="60" y="375" font-size="13" font-weight="800" letter-spacing="1" fill="#0f172a">CHEMICAL, MICROBIAL &amp; PURITY TEST FINDINGS</text>

  <rect x="60" y="390" width="680" height="425" rx="4" fill="#ffffff" stroke="#cbd5e1" stroke-width="1"/>
  <rect x="60" y="390" width="680" height="35" rx="4" fill="#0f172a"/>

  <text x="80" y="412" font-size="11" font-weight="700" fill="#ffffff">PARAMETER / ASSAY</text>
  <text x="320" y="412" font-size="11" font-weight="700" fill="#ffffff">OBSERVED VALUE</text>
  <text x="500" y="412" font-size="11" font-weight="700" fill="#ffffff">SPECIFICATION LIMIT</text>
  <text x="700" y="412" text-anchor="end" font-size="11" font-weight="700" fill="#ffffff">RESULT</text>

  <line x1="60" y1="480" x2="740" y2="480" stroke="#f1f5f9" stroke-width="1"/>
  <text x="80" y="452" font-size="12" font-weight="700" fill="#1e293b">Ashwagandha Whole Root HPLC</text>
  <text x="80" y="468" font-size="10" fill="#64748b">Withanolides standardized fingerprint</text>
  <text x="320" y="460" font-size="12" fill="#0f172a">Conforms to standard</text>
  <text x="500" y="460" font-size="11" fill="#64748b">Positive active assay</text>
  <text x="700" y="460" text-anchor="end" font-size="12" font-weight="800" fill="#059669">PASSED</text>

  <line x1="60" y1="535" x2="740" y2="535" stroke="#f1f5f9" stroke-width="1"/>
  <text x="80" y="507" font-size="12" font-weight="700" fill="#1e293b">Purified Shilajit Mineral Profile</text>
  <text x="80" y="523" font-size="10" fill="#64748b">Fulvic acid &amp; ionic humic trace assay</text>
  <text x="320" y="515" font-size="12" fill="#0f172a">Mineral standard match</text>
  <text x="500" y="515" font-size="11" fill="#64748b">Pharmacopoeia standard</text>
  <text x="700" y="515" text-anchor="end" font-size="12" font-weight="800" fill="#059669">PASSED</text>

  <line x1="60" y1="590" x2="740" y2="590" stroke="#f1f5f9" stroke-width="1"/>
  <text x="80" y="562" font-size="12" font-weight="700" fill="#1e293b">Heavy Metals: Lead (Pb) ICP-MS</text>
  <text x="80" y="578" font-size="10" fill="#64748b">Inductively coupled plasma spectroscopy</text>
  <text x="320" y="570" font-size="12" font-weight="700" fill="#059669">&lt; 0.01 ppm</text>
  <text x="500" y="570" font-size="11" fill="#64748b">Max 2.50 ppm</text>
  <text x="700" y="570" text-anchor="end" font-size="12" font-weight="800" fill="#059669">PASSED</text>

  <line x1="60" y1="645" x2="740" y2="645" stroke="#f1f5f9" stroke-width="1"/>
  <text x="80" y="617" font-size="12" font-weight="700" fill="#1e293b">Heavy Metals: Arsenic (As) ICP-MS</text>
  <text x="80" y="633" font-size="10" fill="#64748b">Toxic metal trace screening</text>
  <text x="320" y="625" font-size="12" font-weight="700" fill="#059669">&lt; 0.02 ppm</text>
  <text x="500" y="625" font-size="11" fill="#64748b">Max 1.00 ppm</text>
  <text x="700" y="625" text-anchor="end" font-size="12" font-weight="800" fill="#059669">PASSED</text>

  <line x1="60" y1="700" x2="740" y2="700" stroke="#f1f5f9" stroke-width="1"/>
  <text x="80" y="672" font-size="12" font-weight="700" fill="#1e293b">Heavy Metals: Mercury (Hg) ICP-MS</text>
  <text x="80" y="688" font-size="10" fill="#64748b">Cold vapor atomic spectroscopy</text>
  <text x="320" y="680" font-size="12" font-weight="700" fill="#059669">&lt; 0.005 ppm</text>
  <text x="500" y="680" font-size="11" fill="#64748b">Max 0.10 ppm</text>
  <text x="700" y="680" text-anchor="end" font-size="12" font-weight="800" fill="#059669">PASSED</text>

  <line x1="60" y1="755" x2="740" y2="755" stroke="#f1f5f9" stroke-width="1"/>
  <text x="80" y="727" font-size="12" font-weight="700" fill="#1e293b">Pathogens: Salmonella &amp; E. Coli</text>
  <text x="80" y="743" font-size="10" fill="#64748b">Selective microbiological culture</text>
  <text x="320" y="735" font-size="12" font-weight="700" fill="#059669">Absent / 10g</text>
  <text x="500" y="735" font-size="11" fill="#64748b">Must be Absent</text>
  <text x="700" y="735" text-anchor="end" font-size="12" font-weight="800" fill="#059669">PASSED</text>

  <text x="80" y="782" font-size="12" font-weight="700" fill="#1e293b">Synthetic Additives &amp; Steroid Screen</text>
  <text x="80" y="798" font-size="10" fill="#64748b">LC-MS/MS negative screening</text>
  <text x="320" y="790" font-size="12" font-weight="700" fill="#059669">Zero Detected</text>
  <text x="500" y="790" font-size="11" fill="#64748b">Zero Tolerance</text>
  <text x="700" y="790" text-anchor="end" font-size="12" font-weight="800" fill="#059669">PASSED</text>

  <g transform="translate(100, 875)">
    <circle cx="50" cy="50" r="46" fill="#ecfdf5" stroke="#059669" stroke-width="2"/>
    <circle cx="50" cy="50" r="41" fill="none" stroke="#059669" stroke-width="1" stroke-dasharray="3,2"/>
    <text x="50" y="42" text-anchor="middle" font-size="8" font-weight="800" fill="#059669" letter-spacing="1">NURA HERBEX</text>
    <text x="50" y="54" text-anchor="middle" font-size="11" font-weight="900" fill="#059669">VERIFIED</text>
    <text x="50" y="66" text-anchor="middle" font-size="7" font-weight="700" fill="#059669" letter-spacing="1">LAB SEAL</text>
  </g>

  <text x="440" y="895" font-size="11" font-weight="700" fill="#64748b">Authorized Quality Assurance Signatory:</text>
  <text x="440" y="920" font-size="16" font-weight="800" fill="#0f172a">Dr. K. S. Raman, Ph.D.</text>
  <text x="440" y="940" font-size="11" fill="#64748b">Senior Laboratory Analyst &amp; Formulations Auditor</text>
  <text x="440" y="960" font-size="10" font-family="monospace" fill="#94a3b8">HASH: NH-SHA256-{bNo}</text>

  <line x1="60" y1="995" x2="740" y2="995" stroke="#e2e8f0" stroke-width="1"/>
  <text x="400" y="1020" text-anchor="middle" font-size="10" fill="#94a3b8">This authenticated laboratory certificate confirms batch compliance with Nura Herbex clinical standards.</text>
</svg>
""";
        return "data:image/svg+xml;utf8," + Uri.EscapeDataString(svg);
    }
}
