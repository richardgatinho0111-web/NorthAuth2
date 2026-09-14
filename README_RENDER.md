# NorthAuth — Painel Web + API

Projeto pronto para deploy no Render usando Docker.

## Render
- Runtime: Docker
- Health check: `/api/health`
- Porta interna: `8080`
- Painel: `/`

## Variável obrigatória
No Render, configure:

`NorthAuth__ApiKey` = a mesma API Key usada pelos clientes NorthAuth.

O `appsettings.json` contém `NORTH-LOCAL-KEY` apenas como fallback para desenvolvimento. Para produção, prefira a variável do Render.

## Painel
Ao abrir o serviço, o painel solicita o usuário e senha do painel.

Primeiro acesso quando `Data/users.json` ainda não existe:
- Usuário: `admin`
- Senha: `North@123`

Troque a senha depois criando/gerenciando usuários pelo painel.

## Recursos
- Dashboard
- Aplicações
- ID da aplicação / Account Owner ID / Client Secret
- Geração de 1 a 500 Keys
- Máscara de Key
- 1, 3, 7, 30 dias, Lifetime e duração personalizada
- Copiar Keys
- Pesquisa e filtros
- Banir/Desbanir
- Resetar HWID
- Excluir licença
- Editar licença
- Usuários do painel
- Criar/ativar/desativar/excluir usuários
- Health check

## Observação de persistência
O projeto usa arquivos JSON em `Data/`. Em hospedagens com filesystem efêmero, esses dados podem ser perdidos após restart/redeploy. Para produção, use banco de dados/persistência apropriada.
