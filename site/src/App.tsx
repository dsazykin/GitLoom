import { useEffect } from 'react';
import { Route, Routes, useLocation } from 'react-router-dom';
import { Nav } from './components/Nav';
import { Footer } from './components/Footer';
import { Home } from './pages/Home';
import { Client } from './pages/Client';
import { Pro } from './pages/Pro';
import { Weave } from './pages/Weave';
import { Contact } from './pages/Contact';
import { Waitlist } from './pages/Waitlist';
import { NotFound } from './pages/NotFound';

const TITLES: Record<string, string> = {
  '/': 'GitLoom — the native Git client for the agent era',
  '/client': 'Git Client — free, native, no login · GitLoom',
  '/pro': 'GitLoom Pro — run a swarm of coding agents, keep control',
  '/weave': 'GitLoom Weave — describe it, watch it woven',
  '/contact': 'Contact · GitLoom',
  '/waitlist': 'Join the waitlist · GitLoom',
};

export default function App() {
  const { pathname } = useLocation();

  useEffect(() => {
    window.scrollTo(0, 0);
    document.title = TITLES[pathname] ?? 'GitLoom';
  }, [pathname]);

  return (
    <>
      <a href="#main" className="visually-hidden">
        Skip to content
      </a>
      <Nav />
      <main id="main">
        <Routes>
          <Route path="/" element={<Home />} />
          <Route path="/client" element={<Client />} />
          <Route path="/pro" element={<Pro />} />
          <Route path="/weave" element={<Weave />} />
          <Route path="/contact" element={<Contact />} />
          <Route path="/waitlist" element={<Waitlist />} />
          <Route path="*" element={<NotFound />} />
        </Routes>
      </main>
      <Footer />
    </>
  );
}
