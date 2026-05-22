import { defineConfig, devices } from '@playwright/test';

const WEB_URL = process.env.WEB_BASE_URL ?? 'http://localhost:5272';
const BACKEND_URL = process.env.BACKEND_BASE_URL ?? 'http://localhost:5070';
const REPO_ROOT = '../..';

export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  fullyParallel: false,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: WEB_URL,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  webServer: [
    {
      command: `dotnet run --project ${REPO_ROOT}/NoteAssistant.KnowledgeGraph.Backend/NoteAssistant.KnowledgeGraph.Backend.csproj --urls ${BACKEND_URL}`,
      url: `${BACKEND_URL}/swagger`,
      reuseExistingServer: true,
      timeout: 120_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
      },
      stdout: 'pipe',
      stderr: 'pipe',
    },
    {
      command: `dotnet run --project ${REPO_ROOT}/NoteAssistant.KnowledgeGraph.Web/NoteAssistant.KnowledgeGraph.Web.csproj --urls ${WEB_URL}`,
      url: WEB_URL,
      reuseExistingServer: true,
      timeout: 120_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
      },
      stdout: 'pipe',
      stderr: 'pipe',
    },
  ],
});
