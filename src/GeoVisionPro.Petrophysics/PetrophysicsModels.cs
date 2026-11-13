using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Domain;

namespace GeoVisionPro.Petrophysics;

public enum VshaleMethod { Linear, LarionovTertiary, LarionovOlder, Clavier, Steiber }
public enum PorosityMethod { Density, Sonic, SonicRaymer, NeutronDensity, Neutron }
public enum SaturationMethod { Archie, Simandoux, Indonesia }

/// <summary>Parameters of a petrophysical evaluation (all documented with typical defaults for clastics).</summary>
public sealed record PetroParameters
{
    public double GrClean { get; init; } = 30;        // gAPI, clean sand line
    public double GrShale { get; init; } = 120;       // gAPI, shale line
    public VshaleMethod VshMethod { get; init; } = VshaleMethod.Linear;
    public PorosityMethod PhiMethod { get; init; } = PorosityMethod.NeutronDensity;
    public double RhoMatrix { get; init; } = 2.65;    // g/cc
    public double RhoFluid { get; init; } = 1.0;      // g/cc
    public double RhoShale { get; init; } = 2.45;     // g/cc
    public double DtMatrix { get; init; } = 55.5;     // us/ft
    public double DtFluid { get; init; } = 189;       // us/ft
    public double NphiShale { get; init; } = 0.35;    // v/v
    public SaturationMethod SwMethod { get; init; } = SaturationMethod.Archie;
    public double Rw { get; init; } = 0.05;           // ohm·m at formation temperature
    public double Rsh { get; init; } = 2.0;           // ohm·m
    public double A { get; init; } = 1.0;             // tortuosity
    public double M { get; init; } = 2.0;             // cementation exponent
    public double N { get; init; } = 2.0;             // saturation exponent
    // net pay cut-offs
    public double VshCutoff { get; init; } = 0.4;
    public double PhiCutoff { get; init; } = 0.08;
    public double SwCutoff { get; init; } = 0.6;
}

public static class PetrophysicsModels
{
    /// <summary>Gamma-ray index I_GR = (GR − GR_clean)/(GR_shale − GR_clean), clipped to [0,1].</summary>
    public static double GammaRayIndex(double gr, double grClean, double grShale) =>
        double.IsNaN(gr) ? double.NaN : Math.Clamp((gr - grClean) / (grShale - grClean), 0, 1);

    /// <summary>Shale volume from I_GR: linear, Larionov (Tertiary: 0.083(2^{3.7I}−1); older: 0.33(2^{2I}−1)), Clavier, Steiber.</summary>
    public static double Vshale(double igr, VshaleMethod m)
    {
        if (double.IsNaN(igr)) return double.NaN;
        double v = m switch
        {
            VshaleMethod.Linear => igr,
            VshaleMethod.LarionovTertiary => 0.083 * (Math.Pow(2, 3.7 * igr) - 1),
            VshaleMethod.LarionovOlder => 0.33 * (Math.Pow(2, 2 * igr) - 1),
            VshaleMethod.Clavier => 1.7 - Math.Sqrt(Math.Max(0, 3.38 - Math.Pow(igr + 0.7, 2))),
            VshaleMethod.Steiber => igr / (3 - 2 * igr),
            _ => igr
        };
        return Math.Clamp(v, 0, 1);
    }

    /// <summary>Density porosity φ_D = (ρ_ma − ρ_b)/(ρ_ma − ρ_fl).</summary>
    public static double DensityPorosity(double rhob, double rhoMa, double rhoFl) => (rhoMa - rhob) / (rhoMa - rhoFl);

    /// <summary>Wyllie sonic porosity φ_S = (Δt − Δt_ma)/(Δt_fl − Δt_ma).</summary>
    public static double SonicPorosity(double dt, double dtMa, double dtFl) => (dt - dtMa) / (dtFl - dtMa);

    /// <summary>Raymer–Hunt–Gardner simplified: φ = 0.625 (Δt − Δt_ma)/Δt.</summary>
    public static double SonicPorosityRaymer(double dt, double dtMa) => 0.625 * (dt - dtMa) / dt;

    /// <summary>Neutron–density porosity (gas-insensitive RMS combination): φ_ND = sqrt((φ_N² + φ_D²)/2).</summary>
    public static double NeutronDensityPorosity(double phiN, double phiD) => Math.Sqrt((phiN * phiN + phiD * phiD) / 2);

    /// <summary>Archie (1942): Sw = (a Rw / (φ^m Rt))^{1/n}. Clean formations only.</summary>
    public static double Archie(double rt, double phi, double rw, double a, double m, double n) =>
        rt <= 0 || phi <= 0 ? double.NaN : Math.Clamp(Math.Pow(a * rw / (Math.Pow(phi, m) * rt), 1 / n), 0, 1);

    /// <summary>
    /// Simandoux (1963) for shaly sands (n = 2): Sw = (a Rw / 2φ^m) [ sqrt((Vsh/Rsh)² + 4φ^m/(a Rw Rt)) − Vsh/Rsh ].
    /// </summary>
    public static double Simandoux(double rt, double phi, double vsh, double rw, double rsh, double a, double m)
    {
        if (rt <= 0 || phi <= 0) return double.NaN;
        double c = Math.Pow(phi, m);
        double t = vsh / rsh;
        return Math.Clamp(a * rw / (2 * c) * (Math.Sqrt(t * t + 4 * c / (a * rw * rt)) - t), 0, 1);
    }

    /// <summary>Indonesia equation (Poupon &amp; Leveaux, 1971): 1/√Rt = [Vsh^{(1−Vsh/2)}/√Rsh + φ^{m/2}/√(aRw)] Sw^{n/2}.</summary>
    public static double Indonesia(double rt, double phi, double vsh, double rw, double rsh, double a, double m, double n)
    {
        if (rt <= 0 || phi <= 0) return double.NaN;
        double term = Math.Pow(vsh, 1 - vsh / 2) / Math.Sqrt(rsh) + Math.Pow(phi, m / 2) / Math.Sqrt(a * rw);
        return Math.Clamp(Math.Pow(1 / (Math.Sqrt(rt) * term), 2 / n), 0, 1);
    }

    /// <summary>Effective porosity from total porosity and shale volume: φ_e = φ_t − Vsh·φ_sh (φ_sh from shale density).</summary>
    public static double EffectivePorosity(double phiT, double vsh, double phiShale) => Math.Max(0, phiT - vsh * phiShale);
}

/// <summary>Results of a well evaluation: computed curves (as new LogCurves) and net pay summary.</summary>
public sealed record PetroResult(LogCurve Vsh, LogCurve PhiT, LogCurve PhiE, LogCurve Sw, LogCurve Shc, LogCurve NetFlag, LogCurve? Lithology,
    NetPaySummary Summary, IReadOnlyList<string> Notes);

public sealed record NetPaySummary(double Top, double Base, double Gross, double NetReservoir, double NetPay, double NetToGross,
    double AveragePhi, double AverageSw, double HydrocarbonPoreThickness);

public static class WellEvaluation
{
    /// <summary>
    /// Deterministic evaluation over [top, base] (MD): Vsh from GR, porosity by the chosen method (shale-corrected for effective
    /// porosity), Sw by the chosen equation, and net reservoir / net pay with cut-offs. Requires GR and RT, plus RHOB/NPHI/DT as needed.
    /// </summary>
    public static PetroResult Evaluate(Well well, PetroParameters p, double top = double.NegativeInfinity, double bottom = double.PositiveInfinity)
    {
        var notes = new List<string>();
        var gr = well.FindCurve(CurveFamily.GammaRay) ?? throw new InvalidOperationException("A gamma-ray curve is required.");
        var rt = well.FindCurve(CurveFamily.ResistivityDeep) ?? throw new InvalidOperationException("A deep resistivity curve is required.");
        var rhob = well.FindCurve(CurveFamily.Density);
        var nphi = well.FindCurve(CurveFamily.NeutronPorosity);
        var dt = well.FindCurve(CurveFamily.Sonic);
        var depths = gr.Depths;
        int n = depths.Length;
        var vsh = new float[n]; var phit = new float[n]; var phie = new float[n]; var sw = new float[n]; var shc = new float[n]; var net = new float[n];
        var method = p.PhiMethod;
        if ((method is PorosityMethod.Density or PorosityMethod.NeutronDensity) && rhob is null) { method = dt is not null ? PorosityMethod.Sonic : throw new InvalidOperationException("Density or sonic log required for porosity."); notes.Add("Density log missing: sonic porosity used."); }
        if (method == PorosityMethod.NeutronDensity && nphi is null) { method = PorosityMethod.Density; notes.Add("Neutron log missing: density porosity used."); }
        if (method is PorosityMethod.Sonic or PorosityMethod.SonicRaymer && dt is null) throw new InvalidOperationException("Sonic log required for sonic porosity.");
        if (method == PorosityMethod.Neutron && nphi is null) throw new InvalidOperationException("Neutron log required.");
        double phiShale = rhob is not null ? Math.Max(0, PetrophysicsModels.DensityPorosity(p.RhoShale, p.RhoMatrix, p.RhoFluid)) : p.NphiShale;
        for (int i = 0; i < n; i++)
        {
            double d = depths[i];
            if (d < top || d > bottom) { vsh[i] = phit[i] = phie[i] = sw[i] = shc[i] = net[i] = float.NaN; continue; }
            double v = PetrophysicsModels.Vshale(PetrophysicsModels.GammaRayIndex(gr.Values[i], p.GrClean, p.GrShale), p.VshMethod);
            double rb = rhob?.ValueAtDepth(d) ?? double.NaN, np = nphi?.ValueAtDepth(d) ?? double.NaN, sdt = dt?.ValueAtDepth(d) ?? double.NaN;
            if (nphi is not null && np > 1.5) np /= 100; // percent → fraction
            double pt = method switch
            {
                PorosityMethod.Density => PetrophysicsModels.DensityPorosity(rb, p.RhoMatrix, p.RhoFluid),
                PorosityMethod.Sonic => PetrophysicsModels.SonicPorosity(sdt, p.DtMatrix, p.DtFluid),
                PorosityMethod.SonicRaymer => PetrophysicsModels.SonicPorosityRaymer(sdt, p.DtMatrix),
                PorosityMethod.Neutron => np,
                _ => PetrophysicsModels.NeutronDensityPorosity(np, PetrophysicsModels.DensityPorosity(rb, p.RhoMatrix, p.RhoFluid))
            };
            pt = Math.Clamp(pt, 0, 0.5);
            double pe = PetrophysicsModels.EffectivePorosity(pt, double.IsNaN(v) ? 0 : v, phiShale);
            double r = rt.ValueAtDepth(d);
            double s = p.SwMethod switch
            {
                SaturationMethod.Archie => PetrophysicsModels.Archie(r, pe, p.Rw, p.A, p.M, p.N),
                SaturationMethod.Simandoux => PetrophysicsModels.Simandoux(r, pe, v, p.Rw, p.Rsh, p.A, p.M),
                _ => PetrophysicsModels.Indonesia(r, pe, v, p.Rw, p.Rsh, p.A, p.M, p.N)
            };
            vsh[i] = (float)v; phit[i] = (float)pt; phie[i] = (float)pe; sw[i] = (float)s; shc[i] = (float)(1 - s);
            bool reservoir = v <= p.VshCutoff && pe >= p.PhiCutoff;
            bool pay = reservoir && s <= p.SwCutoff;
            net[i] = double.IsNaN(v) || double.IsNaN(pe) ? float.NaN : pay ? 2 : reservoir ? 1 : 0;
        }
        var summary = Summarize(depths, vsh, phie, sw, net);
        if (p.SwMethod == SaturationMethod.Archie && vsh.Where(x => !float.IsNaN(x)).DefaultIfEmpty(0).Average() > 0.15)
            notes.Add("Archie applied in shaly intervals (mean Vsh > 0.15): water saturation is likely overestimated; consider Simandoux or Indonesia.");
        notes.Add($"Parameters: GRclean={p.GrClean}, GRshale={p.GrShale}, ρma={p.RhoMatrix}, ρfl={p.RhoFluid}, Rw={p.Rw}, a={p.A}, m={p.M}, n={p.N}.");
        LogCurve C(string m, float[] v, string unit, CurveFamily f, string src) => gr.CloneAs(m, v, unit, f, src);
        return new PetroResult(
            C("VSH", vsh, "v/v", CurveFamily.ShaleVolume, $"Vsh {p.VshMethod}"),
            C("PHIT", phit, "v/v", CurveFamily.TotalPorosity, $"Porosity {method}"),
            C("PHIE", phie, "v/v", CurveFamily.EffectivePorosity, "PHIT − Vsh·φsh"),
            C("SW", sw, "v/v", CurveFamily.WaterSaturation, $"Sw {p.SwMethod}"),
            C("SHC", shc, "v/v", CurveFamily.Other, "1 − Sw"),
            C("NETFLAG", net, "", CurveFamily.Other, "0 non-reservoir, 1 net reservoir, 2 net pay"),
            rhob is not null && nphi is not null ? C("LITHO", LithologyFromNeutronDensity(nphi, rhob, gr, p), "", CurveFamily.Lithology, "Neutron-density/GR lithology") : null,
            summary, notes);
    }

    public static NetPaySummary Summarize(double[] depths, float[] vsh, float[] phie, float[] sw, float[] net)
    {
        double gross = 0, netRes = 0, pay = 0, phiH = 0, swPhiH = 0, hcpt = 0, top = double.NaN, bas = double.NaN;
        for (int i = 0; i < depths.Length; i++)
        {
            if (float.IsNaN(net[i])) continue;
            double h = i + 1 < depths.Length ? depths[i + 1] - depths[i] : i > 0 ? depths[i] - depths[i - 1] : 0;
            if (double.IsNaN(top)) top = depths[i];
            bas = depths[i];
            gross += h;
            if (net[i] >= 1) netRes += h;
            if (net[i] >= 2)
            {
                pay += h; phiH += phie[i] * h; swPhiH += sw[i] * phie[i] * h; hcpt += phie[i] * (1 - sw[i]) * h;
            }
        }
        return new NetPaySummary(top, bas, gross, netRes, pay, gross > 0 ? netRes / gross : 0, pay > 0 ? phiH / pay : double.NaN,
            phiH > 0 ? swPhiH / phiH : double.NaN, hcpt);
    }

    /// <summary>
    /// Simple lithology indicator: 1 = sandstone, 2 = shale, 3 = limestone, 4 = dolomite, 5 = gas-effect sand, based on Vsh and
    /// apparent matrix density from neutron-density (ρ_ma,a = (ρb − φ ρfl)/(1 − φ)). An indicator, not a mineral inversion.
    /// </summary>
    public static float[] LithologyFromNeutronDensity(LogCurve nphi, LogCurve rhob, LogCurve gr, PetroParameters p)
    {
        var res = new float[gr.Count];
        for (int i = 0; i < res.Length; i++)
        {
            double d = gr.Depths[i];
            double np = nphi.ValueAtDepth(d), rb = rhob.ValueAtDepth(d);
            if (np > 1.5) np /= 100;
            double vsh = PetrophysicsModels.Vshale(PetrophysicsModels.GammaRayIndex(gr.Values[i], p.GrClean, p.GrShale), VshaleMethod.Linear);
            if (double.IsNaN(np) || double.IsNaN(rb) || double.IsNaN(vsh)) { res[i] = float.NaN; continue; }
            double phiD = PetrophysicsModels.DensityPorosity(rb, p.RhoMatrix, p.RhoFluid);
            if (vsh > 0.5) { res[i] = 2; continue; }
            if (phiD - np > 0.06) { res[i] = 5; continue; }
            double phi = Math.Clamp((np + phiD) / 2, 0, 0.45);
            double rhoMa = (rb - phi * p.RhoFluid) / (1 - phi);
            res[i] = rhoMa < 2.68 ? 1 : rhoMa < 2.78 ? 3 : 4;
        }
        return res;
    }
}
