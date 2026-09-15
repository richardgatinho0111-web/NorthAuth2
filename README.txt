NORTH AUTH PREMIUM 30.1 — SEGURANÇA + CORREÇÃO DE BUILD

Correção desta versão:
- Corrigido o erro CS1503 no middleware CSRF: CsrfValid agora recebe HttpRequest corretamente.
- Interface mantida no visual atual escuro, moderno, vermelho/preto.

RECURSOS MANTIDOS:
1. Visual atual
2. Sistema de segurança / hardening
3. Login + 2FA
4. Keys / Licenças
5. Dashboard
6. Alertas + Monitoramento
7. Backups
8. Auditoria / Logs
9. Usuários + Permissões
10. Configurações
11. API / Developer

INÍCIO:
1. Execute INICIAR.bat
2. Abra http://localhost:5000
3. Entre com seu usuário do painel

IMPORTANTE:
- Não apague a pasta Data; ela contém os dados do servidor.
- O projeto deve ser executado com o .NET 8 SDK instalado.
- A versão padrão continua usando localhost/http. Para publicação externa, use HTTPS e uma configuração de rede apropriada.

- Seleção de aplicações cadastradas
- App ID, Owner ID e Client Secret com copiar
- Mantidos os dados existentes em Data/

INÍCIO:
1. Execute INICIAR.bat
2. Abra http://localhost:5000
3. Entre com seu usuário do painel

Não apague a pasta Data.


19.2: correção do upload de arquivos e registro de log; dados da pasta Data preservados.


PREMIUM 19.2 — VARIÁVEIS
- Variáveis administrativas separadas por aplicação.
- Tipos: string, number, boolean, JSON e secret.
- Criar, editar, excluir, pesquisar e filtrar por aplicação.
- Permissão Staff: variables.
- Dados salvos em Data\variables.json.


PREMIUM 19.2 — REGRAS
- Regras administrativas separadas por aplicação.
- Tipos: feature, limit, requirement e setting.
- Operadores: igual, diferente, contém, maior/igual e menor/igual.
- Prioridade de 0 a 9999.
- Criar, editar, excluir, pesquisar e filtrar por aplicação.
- Permissão Staff: rules.
- Dados salvos em Data\rules.json.


PREMIUM 29.0 — SEGURANÇA E ALERTAS INTEGRADOS
- Bate-papo interno da equipe do painel.
- Canais geral, suporte e avisos.
- Exclusão da própria mensagem ou por Owner.
- Presença da equipe nos últimos 5 minutos.
- Dados salvos em Data\chat_messages.json.


Premium 28.0 — Monitoramento: painel de saúde, CPU, memória, uptime, sessões, armazenamento e ambiente do servidor.


PREMIUM 29.0
- Proteção contra brute-force: 5 falhas por janela de 15 minutos bloqueiam por 10 minutos.
- 2FA/TOTP para contas administrativas, sem dependência de biblioteca externa.
- Configuração 2FA pelo painel com segredo manual e URI otpauth.
- Login em duas etapas quando 2FA estiver ativo.
- Alertas críticos/altos também entram na central persistente de notificações.
- Resoluções de alerta geram notificação e auditoria.
- Sessões respeitam o período configurado (1–72 horas).


PREMIUM 30.0 — HARDENING DE SEGURANÇA

Objetivo: aumentar bastante a resistência do painel contra ataques comuns. Nenhum software pode ser considerado 100% invulnerável; a proteção depende também de HTTPS, sistema operacional, rede e credenciais.
- Security headers
- Anti-CSRF para mutações do painel
- Cookies de sessão/CSRF com SameSite=Strict e Secure quando HTTPS
- Erros internos sanitizados
- Cache desabilitado para APIs


MUSICA YOUTUBE — PREMIUM 31.2
- A busca por nome agora usa a YouTube Data API v3 para obter um videoId real e incorporar o player oficial.
- Configure a chave no painel em Configurações > Chave YouTube Data API v3 ou use a variável de ambiente YOUTUBE_API_KEY.
- No Google Cloud, habilite YouTube Data API v3 e restrinja a chave à API.
- Alguns vídeos podem continuar indisponíveis se o proprietário não permitir incorporação; a pesquisa tenta priorizar vídeos incorporáveis.
- A chave não é enviada ao navegador durante a pesquisa; a consulta é feita pelo servidor.


31.2 — diagnóstico aprimorado da integração YouTube Data API v3: erros de chave, API desabilitada, cota e restrições agora retornam mensagens específicas em vez de um HTTP 400 genérico. A chave nunca é retornada ao painel.


32.0 PREMIUM — reforço do sistema YouTube e configurações: diagnóstico de erros da API com mensagens específicas, preservação da chave quando o campo fica vazio, botão “Testar YouTube” nas Configurações, validação mais segura dos valores e versão visível atualizada para 32.0 PREMIUM.

PREMIUM 32.1 — CORREÇÃO YOUTUBE
- Adicionado "Salvar e testar" para validar a chave imediatamente.
- O teste aceita a chave digitada sem depender do preenchimento automático do navegador.
- A chave salva no Data/settings.json tem prioridade sobre YOUTUBE_API_KEY; isso evita uma variável antiga sobrescrever uma chave válida salva no painel.
- Se o Google responder com erro, o painel mostra a causa específica quando disponível.
- NÃO substitua a pasta Data do seu servidor atual se ela já contém seus usuários/licenças/configurações.

CONFIGURACAO RADMIN VPN — 30.1
- appsettings.json agora usa http://0.0.0.0:5000 para permitir conexoes pela rede Radmin.
- INICIAR.bat tambem inicia o Kestrel em 0.0.0.0:5000.
- INICIAR-RADMIN.bat mostra o IP 26.x do Radmin, tenta liberar a porta TCP 5000 no Firewall e inicia o servidor.
- No computador do amigo, use http://IP-RADMIN-DO-SERVIDOR:5000.
- O servidor precisa continuar aberto no PC que hospeda.
- Os dois computadores precisam estar na mesma rede do Radmin VPN.
- Nao e necessario trocar localhost nos scripts administrativos locais; eles continuam apontando para o proprio PC.
