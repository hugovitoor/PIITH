# PIITH — protótipo inicial

Aplicação web única para demonstrar três áreas: painel da atendente, totem do paciente e portal médico.

## Requisitos para execução local
- .NET 10 SDK
- PostgreSQL local (ou uma variável `DATABASE_URL` de um banco PostgreSQL acessível)

## Executar
No diretório do projeto:

```bash
dotnet restore
dotnet run
```

Abra a URL indicada no terminal. Para PostgreSQL local, o fallback de desenvolvimento usa `Host=localhost;Port=5432;Database=piith;Username=postgres;Password=postgres`. Configure `ConnectionStrings__Default` ou `DATABASE_URL` conforme seu ambiente.

## Publicar no Railway
1. Envie o conteúdo desta pasta para um repositório GitHub novo.
2. No Railway, crie um projeto e adicione um serviço PostgreSQL.
3. Adicione um serviço a partir do repositório GitHub; o `Dockerfile` será usado para construir a aplicação.
4. Nas variáveis do serviço web, crie `DATABASE_URL` usando a URL de conexão interna fornecida pelo serviço PostgreSQL (referência de variável do Railway, se disponível).
5. Gere um domínio público para o serviço web e aguarde o deploy.
6. Teste `https://SEU-DOMINIO/api/health` e depois a página inicial.

O banco é inicializado com `EnsureCreated` para facilitar a demonstração. Antes de evolução para produção, substituir por migrations EF Core e configurar backup, autenticação, autorização, monitoramento e migrações controladas.

## Endpoints iniciais
- `GET /api/health`
- `GET /api/dashboard`
- `POST /api/prefichas`
- `PATCH /api/prefichas/{id}/priority`
- `PATCH /api/prefichas/{id}/assign`
- `POST /api/doctors/{id}/point`
- `PATCH /api/doctors/{id}/status`

## Limitações e segurança
- Dados de médicos são fictícios e inseridos automaticamente no primeiro início.
- Esta versão NÃO tem autenticação. Não coloque dados reais de pacientes, informações identificáveis ou registros reais de jornada em um endereço público.
- Prioridade é sempre definida por uma pessoa; o sistema não realiza triagem ou diagnóstico.
- A câmera e a IA ainda não estão conectadas; estados dos profissionais são simulados/manual.
- `EnsureCreated` não aplica mudanças futuras de schema em um banco já existente. Adote migrations para próximas versões.
