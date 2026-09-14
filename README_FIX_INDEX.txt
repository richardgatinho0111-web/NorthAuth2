CORREÇÃO DO PAINEL NORTH AUTH NO RENDER

O problema do HTTP 404 em https://northauth.onrender.com/ acontece porque o index.html está na raiz do GitHub, mas o container ASP.NET só procura arquivos estáticos em wwwroot.

CORREÇÃO:
- Program.cs já usa UseDefaultFiles() e UseStaticFiles().
- Dockerfile agora cria /app/wwwroot e copia o index.html da raiz do repositório para /app/wwwroot/index.html.

NO GITHUB:
1. Substitua o Dockerfile atual pelo Dockerfile deste pacote.
2. Mantenha o index.html na raiz do repositório (como já está).
3. Faça Commit changes na branch main.
4. O Render deve fazer Auto-Deploy.
5. Aguarde aparecer Live.
6. Abra https://northauth.onrender.com/

NÃO é necessário criar wwwroot no GitHub. O Docker cria e preenche essa pasta durante o build.
