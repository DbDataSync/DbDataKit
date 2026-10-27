import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import '@dbdatakit/shell/styles.css'
import '@dbdatakit/visuals/styles.css'
import { AdminHostProvider } from '@dbdatakit/admin'
import App from './App.tsx'
import { adminHost } from './adminHost'

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, staleTime: 5000 } },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AdminHostProvider host={adminHost}>
          <App />
        </AdminHostProvider>
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
)
