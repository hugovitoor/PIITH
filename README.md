 PIITH

Protótipo acadêmico para demonstrar o fluxo de pré-fichas, gestão manual da fila e registro demonstrativo de ponto.

## O que foi acrescentado nesta versão
- Edição dos dados de uma pré-ficha pela atendente.
- Mudança de estado: aguardando avaliação, encaminhado, em atendimento, concluído e cancelado.
- Ações para iniciar e concluir atendimentos.
- Ao concluir um atendimento, o médico é marcado como disponível e o horário de liberação é atualizado.
- Histórico dos últimos 30 registros demonstrativos de ponto no portal médico.
- Atualização periódica da tela a cada 10 segundos.

## Limitações importantes
- Este é um protótipo público sem autenticação/autorização; use exclusivamente dados fictícios.
- O registro de ponto é demonstrativo, não é adequado para controle real de jornada.
- A prioridade é definida por uma pessoa; o sistema não faz diagnóstico ou triagem automática.
- O estado do médico ainda é atualizado manualmente; câmeras e IA não estão integradas.
- A inicialização usa `EnsureCreated`; para evoluir o esquema com segurança, implementar EF Core migrations.