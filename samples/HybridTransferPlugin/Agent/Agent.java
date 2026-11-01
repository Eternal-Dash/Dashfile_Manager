/* DashFile adaptation of HybridFileXfer's 1 MiB block scheduling and random-access assembly.
 * Original project/author: https://github.com/weixiansen574/HybridFileXfer — weixiansen574.
 * Upstream revision: 14e75ab30b921b845591e49580cffcb51f3d4b9a. GPL-3.0-or-later.
 * Modified by EternalDash: host-initiated RPC, pinned TLS, per-session authentication,
 * verified transactional writes, idle cleanup. The wire protocol is DashFile-specific.
 */
package com.dashfile.hybrid;

import javax.net.ssl.*;
import java.io.*;
import java.net.*;
import java.nio.file.*;
import java.security.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;

public final class Agent {
    static final int BLOCK = 1024 * 1024;
    static final long MAX_FILE = 1L << 40;
    final String token, serial, uid;
    final SSLServerSocket listener;
    final ExecutorService workers = Executors.newFixedThreadPool(8);
    final ConcurrentHashMap<String, Upload> uploads = new ConcurrentHashMap<>();
    final Set<Socket> sockets = Collections.newSetFromMap(new ConcurrentHashMap<Socket,Boolean>());
    final AtomicInteger activeCalls = new AtomicInteger();
    volatile long lastUse = System.nanoTime();
    volatile boolean running = true;
    final boolean test;

    public static void main(String[] args) throws Exception {
        if (args.length < 5) throw new IllegalArgumentException("p12 password token serial bind [test]");
        new Agent(args).run();
    }
    Agent(String[] a) throws Exception {
        token = a[2]; serial = a[3]; test = a.length > 5 && a[5].equals("test");
        if (!token.matches("[a-fA-F0-9]{64}")) throw new IllegalArgumentException("invalid token");
        String identity;
        try { identity = String.valueOf(Class.forName("android.system.Os").getMethod("getuid").invoke(null)); }
        catch (Exception e) { identity = "desktop-test"; }
        uid = identity;
        KeyStore store = KeyStore.getInstance("PKCS12");
        try (InputStream in = new FileInputStream(a[0])) { store.load(in, a[1].toCharArray()); }
        KeyManagerFactory managers = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        managers.init(store, a[1].toCharArray());
        SSLContext tls = SSLContext.getInstance("TLS"); tls.init(managers.getKeyManagers(), null, new SecureRandom());
        listener = (SSLServerSocket)tls.getServerSocketFactory().createServerSocket(0, 16, InetAddress.getByName(a[4]));
        listener.setEnabledProtocols(new String[] { "TLSv1.2" });
        Thread idle = new Thread(() -> {
            while (running) {
                try { Thread.sleep(5000); } catch (InterruptedException ignored) { return; }
                if (activeCalls.get() == 0 && System.nanoTime() - lastUse > 180_000_000_000L) stop();
            }
        }); idle.setDaemon(true); idle.start();
    }
    void run() throws Exception {
        System.out.println("DHFX_READY " + listener.getLocalPort()); System.out.flush();
        try {
            while (running) { final SSLSocket socket = (SSLSocket)listener.accept(); if (sockets.size() >= 16) { socket.close(); continue; } sockets.add(socket); workers.submit(() -> serve(socket)); }
        } catch (SocketException e) { if (running) throw e; }
        finally { stop(); workers.shutdownNow(); }
    }
    void stop() {
        running = false;
        try { listener.close(); } catch (IOException ignored) { }
        for (Socket s : sockets) try { s.close(); } catch (IOException ignored) { }
        for (Upload u : uploads.values()) u.temp.delete(); uploads.clear();
    }
    void serve(SSLSocket socket) {
        try (SSLSocket s = socket;
             DataInputStream in = new DataInputStream(new BufferedInputStream(s.getInputStream(), 65536));
             DataOutputStream out = new DataOutputStream(new BufferedOutputStream(s.getOutputStream(), 65536))) {
            s.setSoTimeout(60000); s.startHandshake();
            byte[] header = new byte[8]; in.readFully(header);
            boolean valid = Arrays.equals(header, "DFHFX001".getBytes("US-ASCII")) && MessageDigest.isEqual(in.readUTF().getBytes("UTF-8"), token.getBytes("UTF-8"));
            out.writeBoolean(valid); out.flush(); if (!valid) return; s.setSoTimeout(0);
            byte[] work = new byte[BLOCK];
            while (running) {
                int op;
                try { op = in.readUnsignedByte(); } catch (EOFException e) { break; }
                activeCalls.incrementAndGet(); lastUse = System.nanoTime();
                try { handle(op, in, out, work); }
                catch (Exception e) { out.writeBoolean(false); String text = e.getClass().getSimpleName() + ": " + String.valueOf(e.getMessage()); out.writeUTF(text.length() > 4000 ? text.substring(0,4000) : text); }
                finally { out.flush(); lastUse = System.nanoTime(); activeCalls.decrementAndGet(); }
            }
        } catch (Exception ignored) { }
        finally { sockets.remove(socket); }
    }
    static File regular(String path) throws IOException {
        File f = canonical(path);
        if (!f.isFile()) throw new IOException("not a readable regular file: " + path);
        return f;
    }
    static File canonical(String path) throws IOException {
        if (path.indexOf(0) >= 0 || !new File(path).isAbsolute()) throw new IOException("absolute path required");
        File f = new File(path).getCanonicalFile();
        String p = f.getPath();
        if (p.equals("/dev") || p.startsWith("/dev/") || p.equals("/proc") || p.startsWith("/proc/") || p.equals("/sys") || p.startsWith("/sys/")) throw new IOException("special filesystem is not a file transfer target");
        return f;
    }
    static void snapshot(File f, long length, long modified) throws IOException {
        if (f.length() != length || f.lastModified() != modified) throw new IOException("source changed during transfer");
    }
    static String hash(File file) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256"); byte[] buffer = new byte[262144];
        try (InputStream in = new FileInputStream(file)) { int n; while ((n=in.read(buffer)) != -1) { if (Thread.currentThread().isInterrupted()) throw new IOException("cancelled"); digest.update(buffer,0,n); } }
        StringBuilder hex = new StringBuilder(); for (byte b : digest.digest()) hex.append(String.format(Locale.ROOT,"%02X",b & 255)); return hex.toString();
    }
    void handle(int op, DataInputStream in, DataOutputStream out, byte[] work) throws Exception {
        switch (op) {
            case 1: {
                ArrayList<String[]> nets = new ArrayList<>();
                if (test) nets.add(new String[] { "WiFi-test", "127.0.0.1" });
                else {
                    Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
                    while (interfaces != null && interfaces.hasMoreElements() && nets.size() < 8) {
                        NetworkInterface ni = interfaces.nextElement(); String name = ni.getName();
                        if (!ni.isUp() || !(name.startsWith("wlan") || name.startsWith("eth") || name.startsWith("rndis") || name.startsWith("usb"))) continue;
                        Enumeration<InetAddress> addresses = ni.getInetAddresses();
                        while (addresses.hasMoreElements() && nets.size() < 8) { InetAddress ip = addresses.nextElement(); if (ip instanceof Inet4Address && ip.isSiteLocalAddress()) nets.add(new String[] { name, ip.getHostAddress() }); }
                    }
                }
                out.writeBoolean(true); out.writeUTF(serial); out.writeUTF(uid); out.writeInt(BLOCK); out.writeInt(nets.size());
                for (String[] net : nets) { out.writeUTF(net[0]); out.writeUTF(net[1]); } break;
            }
            case 2: {
                File f = regular(in.readUTF()); long size = f.length(); if (size < 0 || size > MAX_FILE) throw new IOException("file too large");
                out.writeBoolean(true); out.writeLong(size); out.writeLong(f.lastModified()); break;
            }
            case 3: {
                String path = in.readUTF(); long offset = in.readLong(); int count = in.readInt(); long expected = in.readLong(); long modified = in.readLong();
                File f = regular(path); snapshot(f,expected,modified);
                if (offset < 0 || count < 0 || count > BLOCK || offset > expected - count) throw new IOException("invalid block range");
                byte[] bytes = work; try (RandomAccessFile file = new RandomAccessFile(f,"r")) { file.seek(offset); file.readFully(bytes,0,count); }
                snapshot(f,expected,modified); out.writeBoolean(true); out.writeInt(count); out.write(bytes,0,count); break;
            }
            case 4: {
                File target = canonical(in.readUTF()); long size = in.readLong(); long modified = in.readLong();
                if (size < 0 || size > MAX_FILE || !target.getParentFile().isDirectory() || (target.exists() && !target.isFile())) throw new IOException("invalid upload destination");
                long available = target.getParentFile().getUsableSpace(); if (available > 0 && available < size + 67108864) throw new IOException("insufficient free space");
                String id = UUID.randomUUID().toString().replace("-", ""); File temp = new File(target.getParentFile(), ".dashfile-hfx-" + id + ".partial");
                if (!temp.createNewFile()) throw new IOException("temporary file already exists");
                try (RandomAccessFile file = new RandomAccessFile(temp,"rw")) { file.setLength(size); }
                catch (Exception e) { temp.delete(); throw e; }
                uploads.put(id,new Upload(target,temp,size,modified)); out.writeBoolean(true); out.writeUTF(id); break;
            }
            case 5: {
                String id = in.readUTF(); long offset = in.readLong(); int count = in.readInt();
                if (count < 0 || count > BLOCK) throw new IOException("invalid block length");
                byte[] bytes = work; in.readFully(bytes,0,count); Upload u = upload(id);
                if (offset < 0 || offset % BLOCK != 0 || offset >= u.size || count != Math.min(BLOCK,u.size-offset)) throw new IOException("invalid upload block range");
                synchronized (u) { int index = (int)(offset/BLOCK); if (u.blocks.get(index)) throw new IOException("duplicate block"); try (RandomAccessFile file = new RandomAccessFile(u.temp,"rw")) { file.seek(offset); file.write(bytes,0,count); } u.blocks.set(index); }
                out.writeBoolean(true); break;
            }
            case 6: {
                String id = in.readUTF(); String expected = in.readUTF(); Upload u = upload(id);
                synchronized (u) {
                    if (u.blocks.cardinality() != (u.size + BLOCK - 1)/BLOCK || !hash(u.temp).equalsIgnoreCase(expected)) throw new IOException("incomplete upload or SHA-256 mismatch");
                    Files.move(u.temp.toPath(),u.target.toPath(),StandardCopyOption.REPLACE_EXISTING,StandardCopyOption.ATOMIC_MOVE);
                    if (u.modified > 0) u.target.setLastModified(u.modified);
                    uploads.remove(id);
                }
                out.writeBoolean(true); break;
            }
            case 7: { Upload u = uploads.remove(in.readUTF()); if (u != null) u.temp.delete(); out.writeBoolean(true); break; }
            case 8: {
                String path = in.readUTF(); long size = in.readLong(); long modified = in.readLong(); File f = regular(path); snapshot(f,size,modified); String sha = hash(f); snapshot(f,size,modified); out.writeBoolean(true); out.writeUTF(sha); break;
            }
            case 9: { out.writeBoolean(true); out.flush(); new Thread(this::stop).start(); break; }
            default: throw new IOException("unknown operation");
        }
    }
    Upload upload(String id) throws IOException { Upload u=uploads.get(id); if(u==null) throw new IOException("upload not found"); return u; }
    static final class Upload {
        final File target,temp; final long size,modified; final BitSet blocks=new BitSet();
        Upload(File t, File p, long s, long m) { target=t;temp=p;size=s;modified=m; }
    }
}
