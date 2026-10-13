# PIITH — versão com front-end fiel às referências

Protótipo acadêmico para a MOSTRA SESI STEAM 2026. Esta versão mantém o conceito visual dos protótipos enviados pela equipe: painel administrativo branco/verde, totem azul com listras diagonais e perguntas em etapas, e portal médico com relógio digital e linha de ECG decorativa.

## Incluído
- Totem do paciente em etapas, com fundo azul listrado e botões arredondados.
- Seleção múltipla de sintomas.
- Pergunta sobre início dos sintomas e intensidade de dor de 0 a 10.
- Sete perguntas complementares com Sim / Não / Não sei:
  - Já procurou atendimento para esse problema anteriormente?
  - Tem alguma doença congênita?
  - Tem alergias?
  - Tem medicamentos de uso contínuo?
  - Realizou cirurgias anteriores?
  - Possui doenças crônicas?
  - Possui algum problema cardíaco ou respiratório?
- Campo opcional de detalhe quando a resposta é Sim.
- Pré-ficha salva no PostgreSQL, incluindo respostas complementares em JSON.
- Painel da atendente com visual branco/verde, cards de pacientes, edição, prioridade humana, encaminhamento e mudança de estado.
- Portal médico com registro demonstrativo de entrada/saída e histórico.
- Página de redirecionamento com médicos disponíveis ordenados pelo tempo desde a última liberação registrada.
- Atualização periódica do painel.
- Coluna `IntakeAnswers` adicionada de forma compatível a uma tabela já existente, sem apagar dados.

## Publicação no Railway
1. Faça backup do repositório atual.
2. Substitua os arquivos pelos deste pacote mantendo `Dockerfile`, `Program.cs`, `PIITH.csproj` e `wwwroot` na raiz do repositório.
3. Confirme que o PostgreSQL do Railway está ativo e que `DATABASE_URL` aponta para ele.
4. Confirme que `ConnectionStrings__Default` não aponta para `localhost`.
5. Faça commit/push para o GitHub e aguarde o redeploy.
6. Teste `/api/health` e depois abra a página principal.
7. Faça uma ficha de teste e confira se ela aparece no painel e se as respostas complementares estão visíveis em “Respostas complementares”.

## Teste local
Requer .NET 10 SDK e PostgreSQL. Configure `DATABASE_URL` ou `ConnectionStrings__Default` e execute `dotnet run`.

## Limitações e privacidade
- Ainda não há autenticação/autorização real: não use dados reais de pacientes ou profissionais.
- O registro de ponto é demonstrativo, não serve como controle oficial de jornada.
- A prioridade clínica é definida pela atendente. O software não realiza diagnóstico ou triagem automatizada.
- As câmeras e a IA ainda não estão conectadas; os painéis de câmera são placeholders.
- Antes de uso real, implementar autenticação, controle de acesso, auditoria, política de retenção, migrations e medidas de conformidade com a LGPD.
