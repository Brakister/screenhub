namespace ScreenLab.Recognition;

/// <summary>
/// Atribui nomes a vários rostos do mesmo quadro respeitando a regra da
/// empresa: <b>cada pessoa ocupa no máximo um rosto</b>.
///
/// Sem essa restrição, dois rostos parecidos tendem a ir para o MESMO vencedor
/// — o primeiro colocado pela similaridade de cada rosto isoladamente. A foto
/// sai com duas pessoas carimbadas com o mesmo nome, que é o pior erro
/// possível. Com a restrição, o segundo rosto é forçado para o segundo melhor
/// candidato, que é o comportamento correto mesmo quando a diferença entre os
/// dois é pequena.
///
/// <para>
/// A escolha é o problema de <b>atribuição ótima</b> (cada rosto para no máximo
/// um nome, cada nome para no máximo um rosto, maximizar a soma das
/// similaridades) — resolvido aqui pelo algoritmo húngaro. A alternativa
/// gulosa (ordenar os pares por similaridade e ir escalando) dá solução
/// subótima: ela pode travar o rosto certo ao escolher primeiro o par mais
/// alto, quando um par levemente menor abriria uma solução melhor no total.
/// </para>
///
/// <para>
/// O caso de 1 rosto é o de sempre (não há restrição a violar), então o
/// caminho comum não muda.
/// </para>
/// </summary>
public static class FaceAssignment
{
    /// <summary>
    /// Resolve a atribuição. <paramref name="rankings"/> traz, para cada rosto,
    /// os candidatos ordenados por similaridade decrescente.
    /// </summary>
    /// <param name="rankings">
    /// Uma entrada por rosto detectado. Cada entrada é a lista
    /// (nome, cosseno) daquele rosto contra toda a galeria, do melhor para
    /// o pior. Rostos sem nenhum candidato entram como lista vazia.
    /// </param>
    /// <returns>
    /// Uma atribuição por rosto, na mesma ordem da entrada. Nome null
    /// significa que nenhum nome sobrou (ou nenhum candidato é usável).
    /// </returns>
    public static List<string?> Assign(
        IReadOnlyList<IReadOnlyList<(string Name, float Cosine)>> rankings)
    {
        int faces = rankings.Count;
        var result = new List<string?>(new string?[faces]);
        if (faces == 0) return result;

        // Um rosto só: nada a desambiguar, é o caminho de sempre.
        if (faces == 1)
        {
            if (rankings[0].Count > 0)
                result[0] = rankings[0][0].Name;
            return result;
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < faces; i++)
            foreach (var (name, _) in rankings[i])
                if (seen.Add(name)) names.Add(name);

        if (names.Count == 0) return result;

        int people = names.Count;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int p = 0; p < people; p++) index[names[p]] = p;

        // score[rosto][pessoa] = maior cosseno daquele rosto com aquela pessoa.
        var score = new float[faces, people];
        for (int i = 0; i < faces; i++)
            foreach (var (name, cos) in rankings[i])
            {
                int p = index[name];
                if (cos > score[i, p]) score[i, p] = cos;
            }

        // A matriz precisa ser quadrada. Completa com zero: cosseno 0 é "nada
        // a ver", então as células de enchimento nunca ganham de um par real.
        int n = Math.Max(faces, people);
        var value = new float[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i < faces && j < people) value[i, j] = score[i, j];

        foreach (var (face, person) in Hungarian(value))
        {
            if (face < faces && person < people && score[face, person] > 0f)
                result[face] = names[person];
        }

        return result;
    }

    /// <summary>
    /// Algoritmo húngaro (potenciais, O(n³)) para atribuição quadrada que
    /// MAXIMIZA a soma dos valores. Converte para custo antes, já que o método
    /// é escrito para minimizar.
    /// </summary>
    /// <returns>Pares (linha, coluna) escolhidos, um por linha.</returns>
    private static IEnumerable<(int Row, int Col)> Hungarian(float[,] value)
    {
        int n = value.GetLength(0);

        // Maximizar valor == minimizar (max - valor), que fica >= 0.
        float max = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (value[i, j] > max) max = value[i, j];
        if (!float.IsFinite(max)) max = 0f;

        var cost = new float[n + 1, n + 1];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                cost[i + 1, j + 1] = max - value[i, j];

        var u = new float[n + 1];
        var v = new float[n + 1];
        var p = new int[n + 1];    // p[j] = linha casada com a coluna j
        var way = new int[n + 1];

        for (int i = 1; i <= n; i++)
        {
            p[0] = i;
            int j0 = 0;
            var minv = new float[n + 1];
            var used = new bool[n + 1];
            for (int j = 1; j <= n; j++) minv[j] = float.PositiveInfinity;

            do
            {
                used[j0] = true;
                int i0 = p[j0];
                float delta = float.PositiveInfinity;
                int j1 = 0;

                for (int j = 1; j <= n; j++)
                {
                    if (used[j]) continue;
                    float cur = cost[i0, j] - u[i0] - v[j];
                    if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                    if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                }

                for (int j = 0; j <= n; j++)
                {
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                    else minv[j] -= delta;
                }

                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                int j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        for (int j = 1; j <= n; j++)
            if (p[j] != 0)
                yield return (p[j] - 1, j - 1);
    }
}
