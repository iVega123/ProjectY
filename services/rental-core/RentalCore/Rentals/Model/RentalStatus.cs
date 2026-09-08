namespace RentalOperations.Model;

/// <summary>
/// As três situações que a coluna <c>status</c> aceita, e nada além delas.
///
/// Havia uma quarta, Quarantined, que existia para os aluguéis duplicados que a
/// migração de índice do MongoDB encontrava e punha de lado. Ela sai junto com o
/// MongoDB: no schema alvo a duplicata não chega a existir -- o índice único
/// parcial recusa a segunda escrita -- então não há o que separar para revisão
/// depois. Manter o valor sem um caminho que o produza seria guardar um estado
/// que ninguém sabe mais alcançar.
/// </summary>
public enum RentalStatus
{
    Active,
    Completed,
    Cancelled
}
