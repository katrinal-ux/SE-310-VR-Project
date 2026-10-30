using System;
using System.Collections.Generic;
using UnityEngine;

public class TrussResult
{
    public bool success;
    public string message;

    public double[] displacements;   // size 2*nodes: [n0x, n0y, n1x, n1y, ...]
    public double[] forces;          // size 2*nodes: applied loads at free DOFs, reactions at constrained DOFs
    public double[] axialForce;      // per member, + = tension, - = compression
    public double[] stress;          // axialForce / area (Pa)
    public double[] length;          // per member (m)
}

public static class TrussSolver
{
    // Same data layout as the Python arrays:
    //   nodes:       (x, y) per node
    //   elems:       [member, 0] = near node, [member, 1] = far node
    //   modE, area:  one value per member
    //   dispCon:     (node, direction, value)  direction 1 = x, 2 = y
    //   fval:        (node, direction, value)  direction 1 = x, 2 = y
    public static TrussResult Solve(
        Vector2[] nodes,
        int[,] elems,
        double[] modE,
        double[] area,
        (int node, int dir, double value)[] dispCon,
        (int node, int dir, double value)[] fval)
    {
        var result = new TrussResult();

        int nElem = elems.GetLength(0);
        int nNode = nodes.Length;
        int nDof = nNode * 2;

        // ---- Validation ----
        if (modE.Length != nElem || area.Length != nElem)
            return Fail(result, "modE and area must have one entry per member.");

        for (int e = 0; e < nElem; e++)
        {
            if (elems[e, 0] < 0 || elems[e, 0] >= nNode || elems[e, 1] < 0 || elems[e, 1] >= nNode)
                return Fail(result, $"Member {e} references a node that doesn't exist.");
        }
        foreach (var c in dispCon)
            if (c.node < 0 || c.node >= nNode || c.dir < 1 || c.dir > 2)
                return Fail(result, $"Support entry (node {c.node}, dir {c.dir}) is invalid.");
        foreach (var f in fval)
            if (f.node < 0 || f.node >= nNode || f.dir < 1 || f.dir > 2)
                return Fail(result, $"Load entry (node {f.node}, dir {f.dir}) is invalid.");

        // ---- Problem initialization ----
        double[] uDisp = new double[nDof];
        double[] forces = new double[nDof];
        double[,] stiffness = new double[nDof, nDof];
        double[] lengths = new double[nElem];

        // ---- Build displacement array (known DOFs) ----
        var kdofList = new List<int>();
        foreach (var c in dispCon)
        {
            int dof = c.node * 2 + c.dir - 1;
            if (!kdofList.Contains(dof)) kdofList.Add(dof);
            uDisp[dof] = c.value;
        }

        // ---- Build force array ----
        foreach (var f in fval)
            forces[f.node * 2 + f.dir - 1] = f.value;

        // ---- Known / unknown DOFs ----
        int[] kdof = kdofList.ToArray();
        var ukdofList = new List<int>();
        for (int i = 0; i < nDof; i++)
            if (!kdofList.Contains(i)) ukdofList.Add(i);
        int[] ukdof = ukdofList.ToArray();

        // ---- Steps 1-3: element stiffness, transform, assemble ----
        for (int e = 0; e < nElem; e++)
        {
            int n0 = elems[e, 0];
            int n1 = elems[e, 1];
            int[] elemDof = { n0 * 2, n0 * 2 + 1, n1 * 2, n1 * 2 + 1 };

            double xa = nodes[n1].x - nodes[n0].x;
            double ya = nodes[n1].y - nodes[n0].y;
            double len = Math.Sqrt(xa * xa + ya * ya);
            if (len < 1e-9)
                return Fail(result, $"Member {e} has zero length.");

            double lx = xa / len;
            double ly = ya / len;
            double k = modE[e] * area[e] / len;

            // k2 = T^T * ke * T, with ke = k*[[1,-1],[-1,1]] and T = [[lx,ly,0,0],[0,0,lx,ly]]
            double[,] k2 =
            {
                {  lx*lx*k,  lx*ly*k, -lx*lx*k, -lx*ly*k },
                {  lx*ly*k,  ly*ly*k, -lx*ly*k, -ly*ly*k },
                { -lx*lx*k, -lx*ly*k,  lx*lx*k,  lx*ly*k },
                { -lx*ly*k, -ly*ly*k,  lx*ly*k,  ly*ly*k }
            };

            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    stiffness[elemDof[i], elemDof[j]] += k2[i, j];

            lengths[e] = len;
        }

        // ---- Step 4: partition and solve ----
        int nu = ukdof.Length;
        int nk = kdof.Length;

        double[,] k11 = new double[nu, nu];
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nu; j++)
                k11[i, j] = stiffness[ukdof[i], ukdof[j]];

        double[] fKnown = new double[nu];
        for (int i = 0; i < nu; i++)
        {
            double sum = 0;
            for (int j = 0; j < nk; j++)
                sum += stiffness[ukdof[i], kdof[j]] * uDisp[kdof[j]];
            fKnown[i] = forces[ukdof[i]] - sum;
        }

        double[] uUnknown;
        if (!GaussSolve(k11, fKnown, out uUnknown))
            return Fail(result, "Structure is unstable (singular stiffness matrix). Check supports and bracing.");

        for (int i = 0; i < nu; i++)
            uDisp[ukdof[i]] = uUnknown[i];

        // Reactions at constrained DOFs: forces[k] = K21 * u_u + K22 * u_k
        for (int i = 0; i < nk; i++)
        {
            double sum = 0;
            for (int j = 0; j < nu; j++)
                sum += stiffness[kdof[i], ukdof[j]] * uDisp[ukdof[j]];
            for (int j = 0; j < nk; j++)
                sum += stiffness[kdof[i], kdof[j]] * uDisp[kdof[j]];
            forces[kdof[i]] = sum;
        }

        // ---- Step 5: internal forces and stresses ----
        double[] axial = new double[nElem];
        double[] stress = new double[nElem];
        for (int e = 0; e < nElem; e++)
        {
            int n0 = elems[e, 0];
            int n1 = elems[e, 1];
            double lx = (nodes[n1].x - nodes[n0].x) / lengths[e];
            double ly = (nodes[n1].y - nodes[n0].y) / lengths[e];

            // Displacements projected onto the member axis at each end
            double dN = lx * uDisp[n0 * 2] + ly * uDisp[n0 * 2 + 1];
            double dF = lx * uDisp[n1 * 2] + ly * uDisp[n1 * 2 + 1];

            // Fint[1] in the Python code: + = tension, - = compression
            axial[e] = (modE[e] * area[e] / lengths[e]) * (dF - dN);
            stress[e] = axial[e] / area[e];
        }

        result.success = true;
        result.message = "OK";
        result.displacements = uDisp;
        result.forces = forces;
        result.axialForce = axial;
        result.stress = stress;
        result.length = lengths;
        return result;
    }

    static TrussResult Fail(TrussResult r, string msg)
    {
        r.success = false;
        r.message = msg;
        return r;
    }

    // Gaussian elimination with partial pivoting. Replaces np.linalg.solve.
    // Returns false if the matrix is singular (a mechanism / unstable structure).
    static bool GaussSolve(double[,] A, double[] b, out double[] x)
    {
        int n = b.Length;
        double[,] a = (double[,])A.Clone();
        double[] rhs = (double[])b.Clone();
        x = new double[n];

        // Scale the tolerance to the matrix so stiff (1e9+) values don't cause false singularity
        double maxAbs = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                maxAbs = Math.Max(maxAbs, Math.Abs(a[i, j]));
        double tol = maxAbs * 1e-12;

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            double best = Math.Abs(a[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(a[r, col]) > best)
                {
                    best = Math.Abs(a[r, col]);
                    pivot = r;
                }
            }
            if (best <= tol) return false;

            if (pivot != col)
            {
                for (int j = 0; j < n; j++)
                {
                    double t = a[col, j]; a[col, j] = a[pivot, j]; a[pivot, j] = t;
                }
                double tb = rhs[col]; rhs[col] = rhs[pivot]; rhs[pivot] = tb;
            }

            for (int r = col + 1; r < n; r++)
            {
                double factor = a[r, col] / a[col, col];
                if (factor == 0) continue;
                for (int j = col; j < n; j++)
                    a[r, j] -= factor * a[col, j];
                rhs[r] -= factor * rhs[col];
            }
        }

        for (int i = n - 1; i >= 0; i--)
        {
            double sum = rhs[i];
            for (int j = i + 1; j < n; j++)
                sum -= a[i, j] * x[j];
            x[i] = sum / a[i, i];
        }
        return true;
    }
}