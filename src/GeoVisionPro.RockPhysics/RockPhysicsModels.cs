namespace GeoVisionPro.RockPhysics;

/// <summary>A mineral or fluid end member. Moduli in GPa, density in g/cc.</summary>
public sealed record Constituent(string Name, double BulkModulus, double ShearModulus, double Density)
{
    public static Constituent Quartz { get; } = new("Quartz", 36.6, 45.0, 2.65);
    public static Constituent Calcite { get; } = new("Calcite", 76.8, 32.0, 2.71);
    public static Constituent Dolomite { get; } = new("Dolomite", 94.9, 45.0, 2.87);
    public static Constituent Clay { get; } = new("Clay", 21.0, 7.0, 2.58);
    public static Constituent Feldspar { get; } = new("Feldspar", 37.5, 15.0, 2.62);
    public static Constituent Brine { get; } = new("Brine", 2.8, 0, 1.09);
    public static Constituent Oil { get; } = new("Oil", 1.0, 0, 0.80);
    public static Constituent Gas { get; } = new("Gas", 0.05, 0, 0.20);
}

/// <summary>Elastic properties: velocities (m/s), density (g/cc), moduli (GPa).</summary>
public sealed record ElasticProperties(double Vp, double Vs, double Density)
{
    /// <summary>Bulk modulus K = ρ(Vp² − 4/3 Vs²), GPa.</summary>
    public double BulkModulus => Density * (Vp * Vp - 4.0 / 3 * Vs * Vs) * 1e-6;
    /// <summary>Shear modulus μ = ρVs², GPa.</summary>
    public double ShearModulus => Density * Vs * Vs * 1e-6;
    public double PImpedance => Vp * Density;
    public double SImpedance => Vs * Density;
    public double VpVs => Vs > 0 ? Vp / Vs : double.NaN;
    /// <summary>Poisson's ratio ν = (Vp² − 2Vs²) / (2(Vp² − Vs²)).</summary>
    public double PoissonsRatio => Vs > 0 ? (Vp * Vp - 2 * Vs * Vs) / (2 * (Vp * Vp - Vs * Vs)) : 0.5;
    /// <summary>λρ = Ip² − 2Is² (GPa·g/cc).</summary>
    public double LambdaRho => (PImpedance * PImpedance - 2 * SImpedance * SImpedance) * 1e-6;
    /// <summary>μρ = Is² (GPa·g/cc).</summary>
    public double MuRho => SImpedance * SImpedance * 1e-6;

    public static ElasticProperties FromModuli(double kGpa, double muGpa, double rho) =>
        new(Math.Sqrt(Math.Max(0, (kGpa + 4.0 / 3 * muGpa) * 1e6 / rho)), Math.Sqrt(Math.Max(0, muGpa * 1e6 / rho)), rho);
}

/// <summary>Classical rock physics relations. Assumptions and validity ranges are documented per method.</summary>
public static class RockPhysicsModels
{
    /// <summary>Voigt (iso-strain upper bound) average Σ f_i M_i.</summary>
    public static double Voigt(IReadOnlyList<double> fractions, IReadOnlyList<double> moduli) => fractions.Select((f, i) => f * moduli[i]).Sum();

    /// <summary>Reuss (iso-stress lower bound) average 1 / Σ(f_i / M_i). Zero modulus constituents give 0.</summary>
    public static double Reuss(IReadOnlyList<double> fractions, IReadOnlyList<double> moduli)
    {
        double s = 0;
        for (int i = 0; i < fractions.Count; i++)
        {
            if (fractions[i] == 0) continue;
            if (moduli[i] <= 0) return 0;
            s += fractions[i] / moduli[i];
        }
        return s > 0 ? 1 / s : 0;
    }

    /// <summary>Voigt–Reuss–Hill average (arithmetic mean of the bounds).</summary>
    public static double Hill(IReadOnlyList<double> fractions, IReadOnlyList<double> moduli) => 0.5 * (Voigt(fractions, moduli) + Reuss(fractions, moduli));

    /// <summary>Mineral mixture moduli (VRH) and density (arithmetic).</summary>
    public static Constituent MixMinerals(IReadOnlyList<(Constituent Mineral, double Fraction)> mix)
    {
        double tot = mix.Sum(m => m.Fraction);
        var f = mix.Select(m => m.Fraction / tot).ToList();
        double k = Hill(f, mix.Select(m => m.Mineral.BulkModulus).ToList());
        double mu = Hill(f, mix.Select(m => m.Mineral.ShearModulus).ToList());
        double rho = mix.Select((m, i) => f[i] * m.Mineral.Density).Sum();
        return new Constituent("Mineral mix", k, mu, rho);
    }

    /// <summary>Wood (Reuss) fluid mixing: K_fl = (Σ S_i/K_i)⁻¹, ρ_fl = Σ S_i ρ_i (uniform saturation).</summary>
    public static Constituent MixFluids(double sw, Constituent brine, Constituent hydrocarbon)
    {
        double k = Reuss([sw, 1 - sw], [brine.BulkModulus, hydrocarbon.BulkModulus]);
        return new Constituent("Fluid mix", k, 0, sw * brine.Density + (1 - sw) * hydrocarbon.Density);
    }

    /// <summary>
    /// Gassmann (1951): K_sat = K_dry + (1 − K_dry/K_min)² / (φ/K_fl + (1−φ)/K_min − K_dry/K_min²); μ_sat = μ_dry.
    /// Assumes isotropic, homogeneous mineral, connected pores and low frequency (relaxed fluid pressure).
    /// </summary>
    public static double GassmannSaturated(double kDry, double kMin, double kFluid, double phi)
    {
        double num = Math.Pow(1 - kDry / kMin, 2);
        double den = phi / kFluid + (1 - phi) / kMin - kDry / (kMin * kMin);
        return kDry + num / den;
    }

    /// <summary>Inverse Gassmann: dry-frame bulk modulus from saturated modulus.</summary>
    public static double GassmannDry(double kSat, double kMin, double kFluid, double phi)
    {
        double a = kSat * (phi * kMin / kFluid + 1 - phi) - kMin;
        double b = phi * kMin / kFluid + kSat / kMin - 1 - phi;
        return a / b;
    }

    /// <summary>
    /// Fluid substitution of measured log properties: computes the dry frame with fluid 1 and re-saturates with fluid 2.
    /// Returns new elastic properties; density changes by φ(ρ_fl2 − ρ_fl1).
    /// </summary>
    public static ElasticProperties FluidSubstitution(ElasticProperties insitu, double phi, Constituent mineral, Constituent fluid1, Constituent fluid2)
    {
        double kSat1 = insitu.BulkModulus, mu = insitu.ShearModulus;
        double kDry = GassmannDry(kSat1, mineral.BulkModulus, fluid1.BulkModulus, phi);
        double kSat2 = GassmannSaturated(kDry, mineral.BulkModulus, fluid2.BulkModulus, phi);
        double rho2 = insitu.Density + phi * (fluid2.Density - fluid1.Density);
        return ElasticProperties.FromModuli(kSat2, mu, rho2);
    }

    /// <summary>Wyllie time-average: 1/V = φ/V_fl + (1−φ)/V_ma. Valid for consolidated, brine-saturated clean sands.</summary>
    public static double WyllieVelocity(double phi, double vMatrix = 5500, double vFluid = 1500) => 1 / (phi / vFluid + (1 - phi) / vMatrix);

    /// <summary>Inverse Wyllie: porosity from velocity.</summary>
    public static double WylliePorosity(double v, double vMatrix = 5500, double vFluid = 1500) => (1 / v - 1 / vMatrix) / (1 / vFluid - 1 / vMatrix);

    /// <summary>Gardner et al. (1974): ρ = a·Vp^b (ρ in g/cc, Vp in m/s; a = 0.31, b = 0.25 for brine-saturated sediments).</summary>
    public static double GardnerDensity(double vp, double a = 0.31, double b = 0.25) => a * Math.Pow(vp, b);

    /// <summary>Castagna et al. (1985) mudrock line: Vs = (Vp − 1360)/1.16 (m/s), brine-saturated clastics.</summary>
    public static double CastagnaMudrock(double vp) => (vp - 1360) / 1.16;

    /// <summary>Greenberg–Castagna (1992) Vs for brine-saturated rock from mineral fractions (sandstone/shale/limestone/dolomite) and Vp in km/s internally.</summary>
    public static double GreenbergCastagnaVs(double vp, double fSand, double fShale, double fLime, double fDolo)
    {
        double v = vp / 1000;
        double[][] c =
        [
            [0, 0.80416, -0.85588],          // sandstone
            [0, 0.76969, -0.86735],          // shale
            [-0.05508, 1.01677, -1.03049],   // limestone
            [0, 0.58321, -0.07775],          // dolomite
        ];
        double[] f = [fSand, fShale, fLime, fDolo];
        double tot = f.Sum();
        double arith = 0, harm = 0;
        for (int i = 0; i < 4; i++)
        {
            double vs = c[i][0] * v * v + c[i][1] * v + c[i][2];
            arith += f[i] / tot * vs;
            if (vs > 0) harm += f[i] / tot / vs;
        }
        return 0.5 * (arith + (harm > 0 ? 1 / harm : 0)) * 1000;
    }

    /// <summary>
    /// Hertz–Mindlin dry pack moduli at effective pressure P (MPa) for a random pack with coordination number n,
    /// critical porosity φc and mineral (K, μ, ν). Returns (K_HM, μ_HM) in GPa. Assumes no-slip grain contacts.
    /// </summary>
    public static (double K, double Mu) HertzMindlin(double pressureMpa, double phiC, double coordination, double kMin, double muMin)
    {
        double nu = (3 * kMin - 2 * muMin) / (2 * (3 * kMin + muMin));
        double p = pressureMpa / 1000; // GPa
        double k = Math.Cbrt(coordination * coordination * Math.Pow(1 - phiC, 2) * muMin * muMin * p / (18 * Math.PI * Math.PI * Math.Pow(1 - nu, 2)));
        double mu = (5 - 4 * nu) / (5 * (2 - nu)) * Math.Cbrt(3 * coordination * coordination * Math.Pow(1 - phiC, 2) * muMin * muMin * p / (2 * Math.PI * Math.PI * Math.Pow(1 - nu, 2)));
        return (k, mu);
    }

    /// <summary>
    /// Soft-sand (friable sand) model (Dvorkin &amp; Nur, 1996): modified lower Hashin–Shtrikman bound between the
    /// Hertz–Mindlin point at φc and the mineral point. Returns dry (K, μ) in GPa for φ ≤ φc.
    /// </summary>
    public static (double K, double Mu) SoftSand(double phi, double pressureMpa, double phiC, double coordination, double kMin, double muMin)
    {
        var (khm, muhm) = HertzMindlin(pressureMpa, phiC, coordination, kMin, muMin);
        double r = phi / phiC;
        double k = 1 / (r / (khm + 4.0 / 3 * muhm) + (1 - r) / (kMin + 4.0 / 3 * muhm)) - 4.0 / 3 * muhm;
        double z = muhm / 6 * (9 * khm + 8 * muhm) / (khm + 2 * muhm);
        double mu = 1 / (r / (muhm + z) + (1 - r) / (muMin + z)) - z;
        return (k, mu);
    }

    /// <summary>
    /// Contact cement model (Dvorkin &amp; Nur, 1996): dry moduli of a pack with cement deposited at grain contacts.
    /// cementFraction is the cement volume fraction of the pack (porosity reduced from φc); scheme: cement at contacts (scheme 1).
    /// </summary>
    public static (double K, double Mu) ContactCement(double phi, double phiC, double coordination, double kMin, double muMin, double kCem, double muCem)
    {
        double nuMin = (3 * kMin - 2 * muMin) / (2 * (3 * kMin + muMin));
        double nuCem = (3 * kCem - 2 * muCem) / (2 * (3 * kCem + muCem));
        double mc = kCem + 4.0 / 3 * muCem;
        double alpha = 2 * Math.Pow((phiC - phi) / (3 * coordination * (1 - phiC)), 0.25); // scheme 1: contact cement
        double lamN = 2 * muCem / (Math.PI * muMin) * (1 - nuMin) * (1 - nuCem) / (1 - 2 * nuCem);
        double lamT = muCem / (Math.PI * muMin);
        double an = -0.024153 * Math.Pow(lamN, -1.3646), bn = 0.20405 * Math.Pow(lamN, -0.89008), cn = 0.00024649 * Math.Pow(lamN, -1.9864);
        double sn = an * alpha * alpha + bn * alpha + cn;
        double at = -1e-2 * (2.26 * nuMin * nuMin + 2.07 * nuMin + 2.3) * Math.Pow(lamT, 0.079 * nuMin * nuMin + 0.1754 * nuMin - 1.342);
        double bt = (0.0573 * nuMin * nuMin + 0.0937 * nuMin + 0.202) * Math.Pow(lamT, 0.0274 * nuMin * nuMin + 0.0529 * nuMin - 0.8765);
        double ct = 1e-4 * (9.654 * nuMin * nuMin + 4.945 * nuMin + 3.1) * Math.Pow(lamT, 0.01867 * nuMin * nuMin + 0.4011 * nuMin - 1.8186);
        double st = at * alpha * alpha + bt * alpha + ct;
        double k = coordination * (1 - phiC) * mc * sn / 6;
        double mu = 3.0 / 5 * k + 3.0 / 20 * coordination * (1 - phiC) * muCem * st;
        return (k, mu);
    }

    /// <summary>Hashin–Shtrikman bounds for two-phase mixtures (f1 of phase 1). Returns (K_lower, K_upper, μ_lower, μ_upper).</summary>
    public static (double KLow, double KHigh, double MuLow, double MuHigh) HashinShtrikman(double k1, double mu1, double k2, double mu2, double f1)
    {
        double f2 = 1 - f1;
        double KHS(double ka, double ma, double kb, double fa, double fb) => ka + fb / (1 / (kb - ka) + fa / (ka + 4.0 / 3 * ma));
        double MHS(double ka, double ma, double mb, double fa, double fb) => ma + fb / (1 / (mb - ma) + 2 * fa * (ka + 2 * ma) / (5 * ma * (ka + 4.0 / 3 * ma)));
        // stiffer phase as reference gives the upper bound
        bool oneStiffer = k1 >= k2 && mu1 >= mu2;
        double kUp = oneStiffer ? KHS(k1, mu1, k2, f1, f2) : KHS(k2, mu2, k1, f2, f1);
        double kLo = oneStiffer ? (mu2 > 0 ? KHS(k2, mu2, k1, f2, f1) : Reuss([f1, f2], [k1, k2])) : (mu1 > 0 ? KHS(k1, mu1, k2, f1, f2) : Reuss([f1, f2], [k1, k2]));
        double muUp = oneStiffer ? MHS(k1, mu1, mu2, f1, f2) : MHS(k2, mu2, mu1, f2, f1);
        double softMu = Math.Min(mu1, mu2);
        double muLo = softMu > 0 ? (oneStiffer ? MHS(k2, mu2, mu1, f2, f1) : MHS(k1, mu1, mu2, f1, f2)) : 0;
        return (kLo, kUp, muLo, muUp);
    }

    /// <summary>
    /// Batzle &amp; Wang (1992) brine properties: density (g/cc) and velocity (m/s) for temperature T (°C), pressure P (MPa)
    /// and salinity S (ppm/1e6 → weight fraction). Returns (K GPa, ρ g/cc).
    /// </summary>
    public static Constituent BatzleWangBrine(double tempC, double pressureMpa, double salinityPpm)
    {
        double T = tempC, P = pressureMpa, S = salinityPpm / 1e6;
        double rhoW = 1 + 1e-6 * (-80 * T - 3.3 * T * T + 0.00175 * T * T * T + 489 * P - 2 * T * P + 0.016 * T * T * P - 1.3e-5 * T * T * T * P - 0.333 * P * P - 0.002 * T * P * P);
        double rhoB = rhoW + S * (0.668 + 0.44 * S + 1e-6 * (300 * P - 2400 * P * S + T * (80 + 3 * T - 3300 * S - 13 * P + 47 * P * S)));
        double[,] w =
        {
            { 1402.85, 1.524, 3.437e-3, -1.197e-5 },
            { 4.871, -0.0111, 1.739e-4, -1.628e-6 },
            { -0.04783, 2.747e-4, -2.135e-6, 1.237e-8 },
            { 1.487e-4, -6.503e-7, -1.455e-8, 1.327e-10 },
            { -2.197e-7, 7.987e-10, 5.230e-11, -4.614e-13 }
        };
        double vw = 0;
        for (int i = 0; i < 5; i++) for (int j = 0; j < 4; j++) vw += w[i, j] * Math.Pow(T, i) * Math.Pow(P, j);
        double vb = vw + S * (1170 - 9.6 * T + 0.055 * T * T - 8.5e-5 * T * T * T + 2.6 * P - 0.0029 * T * P - 0.0476 * P * P)
                  + Math.Pow(S, 1.5) * (780 - 10 * P + 0.16 * P * P) - 1820 * S * S;
        double k = rhoB * vb * vb * 1e-6;
        return new Constituent("Brine (Batzle-Wang)", k, 0, rhoB);
    }
}
